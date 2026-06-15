using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
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
    private static readonly object TrayObservationLock = new();
    private static readonly List<PendingCookingCollection> PendingCookingCollections = new();
    private static readonly List<CompletedNormalCookingCollection> CompletedNormalCookingCollections = new();
    private static readonly Dictionary<string, DateTime> TrayObservationFirstSeen = new();
    private static readonly TimeSpan PendingCookingCollectGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingCookingIdleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CompletedNormalCookingRememberTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TrayObservationRetention = TimeSpan.FromMinutes(5);
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

        var autoTakeBeverage = request.AutoTakeBeverage;
        var autoDeliverFood = request.AutoDeliverFood;
        var autoCompleteOrder = request.AutoCompleteOrder;

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
        var foodAlreadyServed = ReadOrderServedFood(runtimeOrder.Order) != null;
        result.ServedFood = foodAlreadyServed;
        result.ServedBeverage = ReadOrderServedBeverage(runtimeOrder.Order) != null;

        if (autoTakeBeverage)
        {
            if (result.ServedBeverage)
            {
                AddSkipped(result, "普客送达酒水", "该订单已经送达酒水，本次不重复处理。");
            }
            else if (request.BeverageId < 0)
            {
                AddFailure(result, "普客送达酒水", "订单没有有效的酒水 ID。");
                if (request.StopOnError) return Finish(result);
            }
            else
            {
                var pendingBeverage = ReadMember(runtimeOrder.Order, "ServedBeverageInAir");
                if (pendingBeverage != null && IsSellable(pendingBeverage, sellableType: 1, id: request.BeverageId))
                {
                    if (TrySetNormalOrderServedBeverage(runtimeOrder.Order, pendingBeverage))
                    {
                        result.ServedBeverage = true;
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Name = "普客送达酒水",
                            Ok = true,
                            Message = $"{request.BeverageName} 已处于订单待送达状态，已同步为订单酒水。",
                        });
                    }
                    else
                    {
                        AddFailure(result, "普客送达酒水", $"{request.BeverageName} 已处于订单待送达状态，但无法写入订单酒水字段。");
                        if (request.StopOnError) return Finish(result);
                    }
                }
                else if (pendingBeverage != null)
                {
                    AddFailure(result, "普客送达酒水", "订单已有其他待送达酒水，暂不自动送达当前酒水。");
                    if (request.StopOnError) return Finish(result);
                }
                else
                {
                    var beverageResult = TryDeliverNormalOrderBeverage(runtimeOrder.Order, request.BeverageId, request.BeverageName);
                    if (beverageResult.Ok)
                    {
                        result.ServedBeverage = true;
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Name = "普客送达酒水",
                            Ok = true,
                            Message = beverageResult.Message,
                        });
                    }
                    else
                    {
                        AddFailure(result, "普客送达酒水", beverageResult.Message);
                        if (request.StopOnError) return Finish(result);
                    }
                }
            }
        }
        else
        {
            AddSkipped(result, "普客送达酒水", "设置已关闭。");
        }

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
                if (autoDeliverFood)
                {
                    if (TrySetNormalOrderServedFood(runtimeOrder.Order, pendingFood))
                    {
                        result.ServedFood = true;
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Name = "普客送达料理",
                            Ok = true,
                            Message = $"目标料理 {request.RecipeName} 已处于订单待送达状态，已同步为订单料理。",
                        });
                    }
                    else
                    {
                        AddFailure(result, "普客送达料理", $"目标料理 {request.RecipeName} 已处于订单待送达状态，但无法写入订单料理字段。");
                        if (request.StopOnError) return Finish(result);
                    }
                }
                else
                {
                    AddSkipped(result, "普客料理", $"目标料理 {request.RecipeName} 已处于订单待送达状态，等待玩家在游戏内确认。");
                }
            }
            else if (pendingFood != null)
            {
                AddFailure(result, "普客料理", $"订单已有其他待送达料理，暂不自动制作 {request.RecipeName}。");
                if (request.StopOnError) return Finish(result);
            }
            else if (TryConfirmCompletedNormalOrderCooking(request.OrderKey, request.DeskCode, expectedFoodId, out var completedMessage))
            {
                if (autoDeliverFood)
                {
                    var deliveryResult = TryDeliverNormalOrderFoodFromStorage(
                        runtimeOrder.Order,
                        request.OrderKey,
                        request.DeskCode,
                        expectedFoodId,
                        request.RecipeName);
                    if (deliveryResult.Ok)
                    {
                        result.ServedFood = true;
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Name = "普客送达料理",
                            Ok = true,
                            Message = deliveryResult.Message,
                        });
                    }
                    else
                    {
                        AddFailure(result, "普客送达料理", deliveryResult.Message);
                        if (request.StopOnError) return Finish(result);
                    }
                }
                else
                {
                    AddSkipped(result, "普客保温箱", completedMessage);
                }
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

        result.ServedFood = ReadOrderServedFood(runtimeOrder.Order) != null;
        result.ServedBeverage = ReadOrderServedBeverage(runtimeOrder.Order) != null;

        if (autoCompleteOrder)
        {
            if (runtimeOrder.Controller == null)
            {
                AddFailure(result, "触发普客评价", "已匹配普客订单，但未找到对应客人控制器，无法调用游戏评价流程。");
                return Finish(result);
            }

            if (!ReadBool(InvokeInstance(runtimeOrder.Order, "get_IsFullfilled", Array.Empty<object?>())))
            {
                AddSkipped(result, "触发普客评价", "订单尚未同时满足料理和酒水，等待下一轮补齐。");
            }
            else
            {
                InvokeInstance(runtimeOrder.Manager, "EvaluateOrder", new object?[] { runtimeOrder.Controller, false, null });
                result.CompletedOrder = true;
                result.Steps.Add(new OrderPreparationStep
                {
                    Name = "触发普客评价",
                    Ok = true,
                    Message = "已调用游戏评价流程完成当前普客订单。",
                });
            }
        }
        else
        {
            AddSkipped(result, "触发普客评价", "设置已关闭。");
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

    public static int ClearPendingCookingCollections()
    {
        lock (PendingCookingLock)
        {
            var count = PendingCookingCollections.Count + CompletedNormalCookingCollections.Count;
            PendingCookingCollections.Clear();
            CompletedNormalCookingCollections.Clear();
            return count;
        }
    }

    private static void AppendAutomationLog(string action, CookingCollectionTarget? target, string message)
    {
        RuntimeAutomationLogService.Append(action, FormatAutomationTarget(target), message);
    }

    public static string ResolveAutomationLogPath()
    {
        return RuntimeAutomationLogService.ResolvePath();
    }

    private static string FormatAutomationTarget(CookingCollectionTarget? target)
    {
        if (target == null) return "target=none";
        return target.Kind == CookingCollectionTargetKind.NormalOrder
            ? $"target=normal desk={target.DeskCode + 1} orderKey={target.OrderKey} food={target.FoodId}/{target.FoodName} guest={target.GuestName}"
            : "target=rare-tray";
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
        result.Prepared = result.Steps.Any(step => step.Ok
            && !step.Skipped
            && step.Name != "选择订单"
            && step.Name != "选择普客订单"
            && step.Name != "匹配普客订单"
            && step.Name != "匹配运行时订单");
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

    internal sealed class NormalStoredFoodSnapshot
    {
        public NormalStoredFoodSnapshot(bool hasStoredFood, bool hasOrderReceipt, int count, string status)
        {
            HasStoredFood = hasStoredFood;
            HasOrderReceipt = hasOrderReceipt;
            Count = count;
            Status = status;
        }

        public bool HasStoredFood { get; }
        public bool HasOrderReceipt { get; }
        public int Count { get; }
        public string Status { get; }
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

}
