using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 在游戏运行时执行订单准备、自动送达酒水、自动开火、出锅直送和上菜评价。
/// </summary>
/// <remarks>
/// 本服务只应由 Unity 主线程调用。入口请求来自本地 API，但实际执行由 <c>StewardOverlayController</c>
/// 调度回游戏线程，以避免后台 HTTP 线程直接访问 IL2CPP 对象造成崩溃或状态竞争。
/// </remarks>
internal static partial class RuntimeOrderPreparationService
{
    private const string DataBaseCoreTypeName = "GameData.Core.Collections.DataBaseCore";
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";
    private const string TileManagerTypeName = "NightScene.Tiles.TileManager";
    private const string PartnerManagerTypeName = "NightScene.PartnerUtility.PartnerManager";
    private const string CookSystemManagerTypeName = "NightScene.CookingUtility.CookSystemManager";
    private const string QteRewardManagerTypeName = "NightScene.CookingUtility.QTERewardManager";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string SellablePropertyHelperTypeName = "GameData.Core.Collections.SellablePropertyHelper";
    private const string MatchedCookComboTypeName = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel+MatchedCookCombo";
    // 游戏料理最多只能携带五个食材槽位，重复材料也会占用多个槽位。
    private const int MaxFoodIngredientCount = 5;
    private static readonly object PendingCookingLock = new();
    // 自动开火后料理不会立即完成，需保存 CookController，后续轮询完成状态再直接送达。
    private static readonly List<PendingCookingCollection> PendingCookingCollections = new();
    // 刚开始料理后给游戏一小段时间生成 CookController 结果，避免过早判定失败。
    private static readonly TimeSpan PendingCookingCollectGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingCookingIdleTimeout = TimeSpan.FromSeconds(90);
    private enum CookingCollectionTargetKind
    {
        RareOrder,
        NormalOrder,
    }

    public static bool HasPendingCookingCollections
    {
        get
        {
            lock (PendingCookingLock)
            {
                return PendingCookingCollections.Count > 0;
            }
        }
    }

    /// <summary>
    /// 按伴随窗口当前推荐结果准备一笔稀客订单。
    /// </summary>
    /// <param name="request">包含目标订单、推荐料理、酒水、额外食材和自动化开关的请求。</param>
    /// <returns>分步骤记录执行结果；失败时包含可展示给 UI 的错误原因。</returns>
    /// <remarks>
    /// 该方法主要执行“准备”动作：直接送达酒水、开始料理和登记出锅后直接送达。
    /// 评价仍由 <see cref="CompleteFirst(OrderPreparationRequest)"/> 在订单满足后触发。
    /// </remarks>
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

        if (request.RecipeFavoritesOnly && request.AutoStartCooking && !request.RecipeFavorite)
        {
            return Fail(result, "收藏料理限定已开启，但当前订单没有匹配的收藏料理。");
        }

        if (request.BeverageFavoritesOnly && request.AutoTakeBeverage && !request.BeverageFavorite)
        {
            return Fail(result, "收藏酒水限定已开启，但当前订单没有匹配的收藏酒水。");
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "选择订单",
            Ok = true,
            Message = $"桌 {request.DeskCode + 1} · {request.GuestName} · 料理 {request.FoodTag} · 酒水 {request.BeverageTag}",
        });

        RuntimeOrderMatch? runtimeOrderCache = null;
        RuntimeOrderMatch GetRuntimeOrder()
        {
            runtimeOrderCache ??= FindRuntimeOrder(request);
            return runtimeOrderCache;
        }

        if (request.AutoTakeBeverage)
        {
            if (request.BeverageId < 0)
            {
                AddFailure(result, "自动送达酒水", "没有可用的推荐酒水。");
                if (request.StopOnError) return Finish(result);
            }
            else
            {
                var runtimeOrder = GetRuntimeOrder();
                if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
                {
                    var diagnostic = string.IsNullOrWhiteSpace(runtimeOrder.Diagnostic) ? "" : $"（{runtimeOrder.Diagnostic}）";
                    AddFailure(result, "自动送达酒水", $"未找到当前稀客订单对象，可能订单已完成、客人已离场或经营状态刚刷新。{diagnostic}");
                    if (request.StopOnError) return Finish(result);
                }
                else if (ReadOrderServedBeverage(runtimeOrder.Order) != null)
                {
                    result.ServedBeverage = true;
                    AddSkipped(result, "自动送达酒水", "订单已有酒水，本次不重复送达。");
                }
                else
                {
                    var beverageResult = TryDeliverOrderBeverage(runtimeOrder, request.BeverageId, request.BeverageName, "稀客订单");
                    if (beverageResult.Ok)
                    {
                        result.ServedBeverage = true;
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Name = "自动送达酒水",
                            Ok = true,
                            Message = beverageResult.Message,
                        });
                    }
                    else
                    {
                        AddFailure(result, "自动送达酒水", beverageResult.Message);
                        if (request.StopOnError) return Finish(result);
                    }
                }
            }
        }
        else
        {
            AddSkipped(result, "自动送达酒水", "设置已关闭。");
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
                var expectedFoodId = request.FoodId >= 0 ? request.FoodId : ResolveFoodIdFromRecipeId(request.RecipeId);
                var target = CookingCollectionTarget.ForRareOrder(request, expectedFoodId);
                var cookingResult = TryStartCooking(request.RecipeId, request.RecipeName, request.ExtraIngredientIds, request.AutoCollectCooking, target);
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
            AddSkipped(result, "自动送达料理", "料理完成后会自动尝试直接送达顾客。");
            if (request.StopOnError) return Finish(result);
        }
        else
        {
            AddSkipped(result, "自动送达料理", "设置已关闭。");
        }

        return Finish(result);
    }

    /// <summary>
    /// 完成当前匹配到的第一笔稀客订单。
    /// </summary>
    /// <param name="request">前端锁定的订单和推荐目标，必须与当前运行时订单匹配。</param>
    /// <returns>上菜、送达和评价调用的步骤结果。</returns>
    /// <remarks>
    /// 稀客料理和酒水现在都由准备链路直接送达；该方法只补送缺失酒水并在游戏判定订单已满足后调用 <c>EvaluateOrder</c>。
    /// </remarks>
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

        var currentFood = ReadOrderServedFood(runtimeOrder.Order);
        var currentBeverage = ReadOrderServedBeverage(runtimeOrder.Order);
        result.ServedFood = currentFood != null;
        result.ServedBeverage = currentBeverage != null;

        if (currentFood != null)
        {
            result.Steps.Add(new OrderPreparationStep
            {
                Name = "送达料理",
                Ok = true,
                Skipped = true,
                Message = "订单已有料理，本次不重复送达。",
            });
        }
        else
        {
            AddSkipped(result, "送达料理", "订单尚未送达料理，等待料理完成后直接送达。");
        }

        var deliveredItemCount = 0;
        if (currentBeverage != null)
        {
            result.Steps.Add(new OrderPreparationStep
            {
                Name = "送达酒水",
                Ok = true,
                Skipped = true,
                Message = "订单已有酒水，本次不重复送达。",
            });
        }
        else if (request.BeverageId < 0)
        {
            AddSkipped(result, "送达酒水", "当前订单没有可用的推荐酒水，等待推荐刷新。");
        }
        else
        {
            var beverageResult = TryDeliverOrderBeverage(runtimeOrder, request.BeverageId, request.BeverageName, "稀客订单");
            if (!beverageResult.Ok)
            {
                AddFailure(result, "送达酒水", beverageResult.Message);
                return Finish(result);
            }

            deliveredItemCount++;
            result.ServedBeverage = true;
            result.Steps.Add(new OrderPreparationStep
            {
                Name = "送达酒水",
                Ok = true,
                Message = beverageResult.Message,
            });
        }

        result.ServedFood = ReadOrderServedFood(runtimeOrder.Order) != null;
        result.ServedBeverage = ReadOrderServedBeverage(runtimeOrder.Order) != null;

        if (!AddPatientRecoveryStepIfNeeded(result, runtimeOrder, deliveredItemCount))
        {
            return Finish(result);
        }

        if (!TryEvaluateOrderIfReady(result, runtimeOrder, "触发上菜评价", "当前订单"))
        {
            return Finish(result);
        }

        return Finish(result);
    }

    /// <summary>
    /// 完成当前匹配到的第一笔普客订单。
    /// </summary>
    /// <param name="request">前端锁定的普客订单、目标料理和酒水。</param>
    /// <returns>普客酒水、料理制作、直接送达和评价的分步骤结果。</returns>
    /// <remarks>
    /// 普客酒水和料理都走统一直接送达提交；料理若尚未出锅，会登记待送达任务并由后续轮询处理。
    /// </remarks>
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

        if (runtimeOrder.Controller == null)
        {
            var diagnostic = string.IsNullOrWhiteSpace(runtimeOrder.Diagnostic) ? "" : $"（{runtimeOrder.Diagnostic}）";
            AddFailure(result, "匹配普客订单", $"已找到桌 {request.DeskCode + 1} 的普客订单，但未读取到可执行客人控制器；该订单可能只残留在 HUD 中，暂不自动送达以避免卡住顾客。{diagnostic}");
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
        var deliveredNormalItemCount = 0;

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
                    var delivery = TryCommitRuntimeDelivery(
                        runtimeOrder,
                        pendingBeverage,
                        RuntimeDeliveryItemKind.Beverage,
                        request.BeverageName);
                    if (delivery.Ok)
                    {
                        deliveredNormalItemCount++;
                        result.ServedBeverage = true;
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Name = "普客送达酒水",
                            Ok = true,
                            Message = $"{request.BeverageName} 已处于订单待送达状态，已按游戏送达流程提交。",
                        });
                    }
                    else
                    {
                        AddFailure(result, "普客送达酒水", delivery.Message);
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
                    var beverageResult = TryDeliverOrderBeverage(runtimeOrder, request.BeverageId, request.BeverageName, "普客订单");
                    if (beverageResult.Ok)
                    {
                        deliveredNormalItemCount++;
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
                    var delivery = TryCommitRuntimeDelivery(
                        runtimeOrder,
                        pendingFood,
                        RuntimeDeliveryItemKind.Food,
                        request.RecipeName);
                    if (delivery.Ok)
                    {
                        deliveredNormalItemCount++;
                        result.ServedFood = true;
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Name = "普客送达料理",
                            Ok = true,
                            Message = $"目标料理 {request.RecipeName} 已处于订单待送达状态，已按游戏送达流程提交。",
                        });
                    }
                    else
                    {
                        AddFailure(result, "普客送达料理", delivery.Message);
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
            else if (HasPendingNormalOrderCooking(request.OrderKey, runtimeOrder.Order, request.DeskCode, expectedFoodId, request.BeverageId, out var pendingMessage))
            {
                var pendingResult = autoDeliverFood
                    ? TryProcessPendingNormalOrderCooking(request.OrderKey, runtimeOrder.Order, request.DeskCode, expectedFoodId, request.BeverageId)
                    : (Found: true, Delivered: false, StepName: "普客开始料理", Message: pendingMessage);
                if (pendingResult.Delivered)
                {
                    result.ServedFood = true;
                    result.Steps.Add(new OrderPreparationStep
                    {
                        Name = "普客送达料理",
                        Ok = true,
                        Message = pendingResult.Message,
                    });
                }
                else
                {
                    AddSkipped(result, string.IsNullOrWhiteSpace(pendingResult.StepName) ? "普客开始料理" : pendingResult.StepName, string.IsNullOrWhiteSpace(pendingResult.Message) ? pendingMessage : pendingResult.Message);
                }
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
                        result.Order.GuestName,
                        request.BeverageId,
                        request.BeverageName,
                        request.AutoCompleteOrder);
                    var autoDeliverCookedFood = request.AutoCollectCooking && autoDeliverFood;
                    var cookingResult = TryStartCooking(recipeId, request.RecipeName, request.ExtraIngredientIds, autoDeliverCookedFood, target);
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
                        AddSkipped(result, "普客送达料理", autoDeliverCookedFood
                            ? "料理已开始制作，完成后会自动直接送达顾客。"
                            : "料理已开始制作，自动送达料理未开启，完成后保留在厨具中等待手动处理。");
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
        if (!AddPatientRecoveryStepIfNeeded(result, runtimeOrder, deliveredNormalItemCount))
        {
            return Finish(result);
        }

        if (autoCompleteOrder)
        {
            if (!TryEvaluateOrderIfReady(result, runtimeOrder, "触发普客评价", "当前普客订单"))
            {
                return Finish(result);
            }
        }
        else
        {
            AddSkipped(result, "触发普客评价", "设置已关闭。");
        }

        return Finish(result);
    }

    /// <summary>
    /// 轮询自动开火后的待直送料理，并在料理完成时直接送达目标订单。
    /// </summary>
    /// <returns>本轮产生的用户可见自动化消息。</returns>
    /// <remarks>
    /// 该方法由 Overlay 的 Update 循环调用，必须保持轻量且容忍游戏对象临时不可用。
    /// 超过空闲超时仍无法直送的待办会被移除，避免永久占用自动化状态。
    /// </remarks>
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
                        ? (true, $"{pending.RecipeName} 出锅直送已停止：{ex.GetBaseException().Message}")
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

    /// <summary>
    /// 清理所有等待出锅后直接送达的料理任务。
    /// </summary>
    /// <returns>被清理的缓存项数量。</returns>
    public static int ClearPendingCookingCollections()
    {
        lock (PendingCookingLock)
        {
            var count = PendingCookingCollections.Count;
            PendingCookingCollections.Clear();
            return count;
        }
    }

    private static void AppendAutomationLog(string action, CookingCollectionTarget? target, string message)
    {
        RuntimeAutomationLogService.Append(action, FormatAutomationTarget(target), message);
    }

    /// <summary>
    /// 取得自动化日志文件路径，供本地 API 返回给伴随窗口。
    /// </summary>
    public static string ResolveAutomationLogPath()
    {
        return RuntimeAutomationLogService.ResolvePath();
    }

    private static string FormatAutomationTarget(CookingCollectionTarget? target)
    {
        if (target == null) return "target=none";
        return target.Kind == CookingCollectionTargetKind.NormalOrder
            ? $"target=normal desk={target.DeskCode + 1} orderKey={target.OrderKey} food={target.FoodId}/{target.FoodName} beverage={target.BeverageId}/{target.BeverageName} guest={target.GuestName}"
            : $"target=rare desk={target.DeskCode + 1} guest={target.GuestName}/{target.GuestId?.ToString() ?? ""} food={target.FoodId}/{target.FoodName} beverage={target.BeverageId}/{target.BeverageName}";
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

    /// <summary>
    /// 将步骤列表归约为订单准备结果。
    /// </summary>
    /// <remarks>
    /// “选择订单”和“匹配订单”只代表定位成功，不算作真正准备行为；这样 UI 可以区分“已执行自动化”
    /// 与“仅确认目标存在”两类结果。
    /// </remarks>
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

    /// <summary>
    /// 等待料理完成并直接送达的上下文。
    /// </summary>
    private sealed class PendingCookingCollection
    {
        public object CookController { get; init; } = new();
        public string RecipeName { get; init; } = "";
        public DateTime CreatedAtUtc { get; init; }
        public CookingCollectionTarget Target { get; init; } = CookingCollectionTarget.ForRareOrder(new OrderPreparationRequest(), -1);
    }

    /// <summary>
    /// 描述自动出锅后的直接送达目标。
    /// </summary>
    private sealed class CookingCollectionTarget
    {
        public CookingCollectionTargetKind Kind { get; private init; }
        public object? Manager { get; private init; }
        public object? Controller { get; private init; }
        public object? Order { get; private init; }
        public string OrderKey { get; private init; } = "";
        public int? GuestId { get; private init; }
        public string FoodTag { get; private init; } = "";
        public string BeverageTag { get; private init; } = "";
        public int FoodId { get; private init; } = -1;
        public string FoodName { get; private init; } = "";
        public int BeverageId { get; private init; } = -1;
        public string BeverageName { get; private init; } = "";
        public int DeskCode { get; private init; } = -1;
        public string GuestName { get; private init; } = "";
        public bool AutoCompleteOrder { get; private init; }

        public static CookingCollectionTarget ForRareOrder(OrderPreparationRequest request, int foodId)
        {
            return new CookingCollectionTarget
            {
                Kind = CookingCollectionTargetKind.RareOrder,
                GuestId = request.GuestId,
                FoodTag = request.FoodTag,
                BeverageTag = request.BeverageTag,
                FoodId = foodId,
                FoodName = request.RecipeName,
                BeverageId = request.BeverageId,
                BeverageName = request.BeverageName,
                DeskCode = request.DeskCode,
                GuestName = request.GuestName,
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
            string guestName,
            int beverageId,
            string beverageName,
            bool autoCompleteOrder)
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
                BeverageId = beverageId,
                BeverageName = beverageName,
                DeskCode = deskCode,
                GuestName = guestName,
                AutoCompleteOrder = autoCompleteOrder,
            };
        }
    }

    /// <summary>
    /// 游戏开火动作的结果，包含可能触发的 QTE 处理结果。
    /// </summary>
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
