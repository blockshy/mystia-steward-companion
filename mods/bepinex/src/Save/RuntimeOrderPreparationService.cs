using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.Core;
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
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string IzakayaConfigureTypeName = "GameData.RunTime.NightSceneUtility.IzakayaConfigure";
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";
    private const string TileManagerTypeName = "NightScene.Tiles.TileManager";
    private const string PartnerManagerTypeName = "NightScene.PartnerUtility.PartnerManager";
    private const string CookSystemManagerTypeName = "NightScene.CookingUtility.CookSystemManager";
    private const string QteRewardManagerTypeName = "NightScene.CookingUtility.QTERewardManager";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string SpecialOrderTypeName = "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder";
    private const string NightSceneDirectorTypeName = "NightScene.NightSceneDirector";
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string SellablePropertyHelperTypeName = "GameData.Core.Collections.SellablePropertyHelper";
    private const string MatchedCookComboTypeName = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel+MatchedCookCombo";
    // 游戏料理最多只能携带五个食材槽位，重复材料也会占用多个槽位。
    private const int MaxFoodIngredientCount = 5;
    private static readonly object AutomationCookingJobLock = new();
    // Each job owns one exact SetCook generation. No operation may touch a later generation on the same cooker.
    private static readonly List<AutomationCookingJob> AutomationCookingJobs = new();
    private static readonly List<AutomationRuntimeEvent> AutomationRuntimeEvents = new();
    private static readonly AutomationSafetyBarrierRegistry UnresolvedAutomationSafetyBarriers = new();
    private static readonly string AutomationRuntimeSessionId = Guid.NewGuid().ToString("N");
    private const int MaxAutomationRuntimeEvents = 64;
    private static long AutomationRuntimeEventSequence;
    private static long AutomationCookingJobSequence;
    private static readonly TimeSpan CookingDeliveryTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MissingTargetRetireDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MissingControllerRetireDelay = TimeSpan.FromSeconds(6);
    private const int MissingTargetRetireAttempts = 6;
    private const int MissingControllerRetireAttempts = 10;
    private const int MaxCookerCleanupAttempts = 6;
    private const int ManualHandoffReadFailureLimit = 3;
    private static readonly TimeSpan ManualHandoffReadFailureGrace = TimeSpan.FromMilliseconds(500);

    public static string AutomationSessionId => AutomationRuntimeSessionId;

    private static class OrderPreparationStepCodes
    {
        public const string BeverageDelivered = "beverage-delivered";
        public const string BeverageDeliveryCommitUncertain = "beverage-delivery-commit-uncertain";
        public const string FoodDeliveryCommitUncertain = "food-delivery-commit-uncertain";
        public const string CookingStarted = "cooking-started";
        public const string CookingStartUnowned = "cooking-start-unowned";
        public const string CookingPending = "cooking-pending";
        public const string CookingMismatchStored = "cooking-mismatch-stored";
        public const string CookingTagsUnreadableStored = "cooking-tags-unreadable-stored";
        public const string CookingResultRemoved = "cooking-result-removed";
        public const string CookingControllerReused = "cooking-controller-reused";
        public const string CookingProgressStalled = "cooking-progress-stalled";
        public const string CookingProgressRegressed = "cooking-progress-regressed";
        public const string CookingResultUnreadable = "cooking-result-unreadable";
        public const string CookingTargetUnavailableStored = "cooking-target-unavailable-stored";
        public const string CookingTargetAlreadyServedStored = "cooking-target-already-served-stored";
        public const string CookingDeliveryBlocked = "cooking-delivery-blocked";
        public const string CookingDeliveryCommitUncertain = "cooking-delivery-commit-uncertain";
        public const string CookingDeliveryCleanupBlocked = "cooking-delivery-cleanup-blocked";
        public const string CookingWarmerCommitUncertain = "cooking-warmer-commit-uncertain";
        public const string CookingWarmerResetBlocked = "cooking-warmer-reset-blocked";
        public const string CookingManualHandoffCompleted = "cooking-manual-handoff-completed";
        public const string CookingManualHandoffUnreadable = "cooking-manual-handoff-unreadable";
        public const string OrderEvaluationStateUnreadable = "order-evaluation-state-unreadable";
        public const string OrderEvaluationCommitUncertain = "order-evaluation-commit-uncertain";
        public const string CookingCancelled = "cooking-cancelled";
        public const string NightBusinessLifecycleUnavailable = "night-business-lifecycle-unavailable";
        public const string FoodDelivered = "food-delivered";
    }

    private enum CookingCollectionTargetKind
    {
        RareOrder,
        NormalOrder,
    }

    private enum AutomationDeliveryFailureKind
    {
        MissingOrder,
        MissingController,
    }

    public static bool HasAutomationCookingJobs
    {
        get
        {
            lock (AutomationCookingJobLock)
            {
                return AutomationCookingJobs.Count > 0;
            }
        }
    }

    public static IReadOnlyList<AutomationRuntimeEvent> SnapshotAutomationRuntimeEvents()
    {
        lock (AutomationCookingJobLock)
        {
            return AutomationRuntimeEvents.ToList();
        }
    }

    public static IReadOnlyList<AutomationCookingJobSnapshot> SnapshotAutomationCookingJobs()
    {
        lock (AutomationCookingJobLock)
        {
            return AutomationCookingJobs.Select(job => job.ToSnapshot()).ToList();
        }
    }

    public static AutomationSafetyBarrierAckResult AcknowledgeAutomationSafetyBarrier(long sequence)
    {
        lock (AutomationCookingJobLock)
        {
            var acknowledgement = UnresolvedAutomationSafetyBarriers.Acknowledge(sequence);
            if (sequence <= 0 || !acknowledgement.Found)
            {
                return new AutomationSafetyBarrierAckResult
                {
                    Ok = false,
                    Sequence = sequence,
                    Error = "未找到对应的未确认自动化安全栅栏；不会解除订单阻断。",
                };
            }

            var acknowledgedSequences = acknowledgement.Sequences.ToHashSet();
            AutomationRuntimeEvents.RemoveAll(runtimeEvent => acknowledgedSequences.Contains(runtimeEvent.Sequence));
            return new AutomationSafetyBarrierAckResult
            {
                Ok = true,
                Sequence = sequence,
                AcknowledgedCount = acknowledgement.Sequences.Count,
                AcknowledgedSequences = acknowledgement.Sequences,
                Status = $"已确认并解除 {acknowledgement.Sequences.Count} 个同订单自动化安全栅栏。",
            };
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
        if (!RuntimeNightBusinessLifecycle.IsActive) return BuildLifecycleUnavailableResult(request, "rare");
        var sessionGeneration = RuntimeNightBusinessLifecycle.Generation;

        var traceId = ResolveRequestTraceId(OrderTraceKind.Rare, request);
        AppendWackyRequestDiagnostic("rare-prepare-start", request, traceId, "rare");
        AppendYuyukoRequestDiagnostic("rare-prepare-start", request, traceId, "rare");
        var result = new OrderPreparationResult
        {
            Order = new OrderPreparationOrder
            {
                TraceId = traceId,
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

        if (TryApplyUnresolvedAutomationSafetyBarrier(result, "rare", traceId, request.OrderKey))
        {
            return Finish(result);
        }

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
        CookingCollectionTarget? actionTarget = null;

        if (request.AutoTakeBeverage)
        {
            result.Automation.Stage = "beverage";
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
                    actionTarget ??= BuildRareAutomationTarget(request);
                    var beverageResult = TryDeliverOrderBeverage(runtimeOrder, request.BeverageId, request.BeverageName, "稀客订单");
                    if (beverageResult.Ok)
                    {
                        result.ServedBeverage = true;
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Code = OrderPreparationStepCodes.BeverageDelivered,
                            Name = "自动送达酒水",
                            Ok = true,
                            Message = beverageResult.Message,
                        });
                    }
                    else
                    {
                        AddFailure(result, "自动送达酒水", beverageResult.Message, beverageResult.Code);
                        RecordOrderSafetyBarrierIfNeeded(
                            beverageResult.Code,
                            actionTarget,
                            beverageResult.Message);
                        if (request.StopOnError || IsAutomationSafetyBarrierCode(beverageResult.Code)) return Finish(result);
                    }
                }
            }
        }
        else
        {
            AddSkipped(result, "自动送达酒水", "设置已关闭。");
        }

        if (!EnsureLifecycleSessionActive(result, sessionGeneration, "送达酒水后")) return Finish(result);

        if (request.AutoStartCooking)
        {
            result.Automation.Stage = "cooking-start";
            if (request.RecipeId < 0)
            {
                AddFailure(result, "自动开始料理", "没有可用的推荐料理。");
                if (request.StopOnError) return Finish(result);
            }
            else
            {
                var expectedFoodId = request.FoodId >= 0 ? request.FoodId : ResolveFoodIdFromRecipeId(request.RecipeId);
                var runtimeOrder = GetRuntimeOrder();
                if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
                {
                    var diagnostic = string.IsNullOrWhiteSpace(runtimeOrder.Diagnostic) ? "" : $"（{runtimeOrder.Diagnostic}）";
                    AddFailure(result, "自动开始料理", $"无法确认当前稀客订单、客人控制器或管理器，已在扣除材料前取消开锅。{diagnostic}");
                    return Finish(result);
                }

                if (!TryReadOrderServedItem(
                        runtimeOrder.Order,
                        RuntimeDeliveryItemKind.Food,
                        out var servedFood,
                        out var servedFoodDiagnostic))
                {
                    AddFailure(
                        result,
                        "自动开始料理",
                        $"无法确认订单是否已经送达料理，已在扣除材料前取消开锅：{servedFoodDiagnostic}");
                    return Finish(result);
                }

                if (servedFood != null)
                {
                    result.ServedFood = true;
                    AddSkipped(result, "自动开始料理", "订单已经送达料理，本次不重复开锅。");
                }
                else
                {
                    if (!TryReadOrderInAirItem(
                            runtimeOrder.Order,
                            RuntimeDeliveryItemKind.Food,
                            out var pendingFood,
                            out var pendingFoodDiagnostic))
                    {
                        AddFailure(
                            result,
                            "自动开始料理",
                            $"无法确认订单是否已有待送达料理，已在扣除材料前取消开锅：{pendingFoodDiagnostic}");
                        return Finish(result);
                    }

                    if (pendingFood != null)
                    {
                        result.Automation.Stage = "cooking-delivery";
                        AddSkipped(result, "自动开始料理", "订单已有待送达料理，本次不重复开锅，等待游戏送达流程确认。");
                        return Finish(result);
                    }

                    var target = actionTarget ??= BuildRareAutomationTarget(request, expectedFoodId);
                    if (TryGetRecentWackyRejectedCookingMessage(target, out var rejectedMessage))
                    {
                        AddSkipped(result, "自动开始料理", rejectedMessage);
                    }
                    else
                    {
                        var cookingResult = TryStartCooking(request.RecipeId, request.RecipeName, request.ExtraIngredientIds, request.AutoCollectCooking, target);
                        if (cookingResult.Ok)
                        {
                            result.Automation.Stage = cookingResult.ExistingJob
                                ? "cooking-delivery"
                                : "cooking-start";
                            result.Automation.JobId = cookingResult.JobId;
                            result.Steps.Add(new OrderPreparationStep
                            {
                                Code = cookingResult.ExistingJob
                                    ? OrderPreparationStepCodes.CookingPending
                                    : OrderPreparationStepCodes.CookingStarted,
                                Name = "自动开始料理",
                                Ok = true,
                                Skipped = cookingResult.ExistingJob,
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
                            AddFailure(result, "自动开始料理", cookingResult.Message, cookingResult.Code);
                            if (request.StopOnError || IsAutomationSafetyBarrierCode(cookingResult.Code)) return Finish(result);
                        }
                    }
                }
            }
        }
        else
        {
            AddSkipped(result, "自动开始料理", "设置已关闭。");
        }

        if (!EnsureLifecycleSessionActive(result, sessionGeneration, "开始料理后")) return Finish(result);

        if (request.AutoCollectCooking)
        {
            AddSkipped(result, "自动送达料理", "料理完成后会自动尝试直接送达顾客。");
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
        if (!RuntimeNightBusinessLifecycle.IsActive) return BuildLifecycleUnavailableResult(request, "rare");
        var sessionGeneration = RuntimeNightBusinessLifecycle.Generation;

        var traceId = ResolveRequestTraceId(OrderTraceKind.Rare, request);
        AppendYuyukoRequestDiagnostic("rare-complete-start", request, traceId, "rare");
        var result = new OrderPreparationResult
        {
            Order = new OrderPreparationOrder
            {
                TraceId = traceId,
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

        if (TryApplyUnresolvedAutomationSafetyBarrier(result, "rare", traceId, request.OrderKey))
        {
            return Finish(result);
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "选择订单",
            Ok = true,
            Message = $"桌 {request.DeskCode + 1} · {request.GuestName} · 料理 {request.FoodTag} · 酒水 {request.BeverageTag}",
        });

        var runtimeOrder = FindRuntimeOrder(request, RuntimeOrderLookupPurpose.Completion);
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
        else if (!request.AutoTakeBeverage)
        {
            AddSkipped(result, "送达酒水", "自动送达酒水未开启，等待玩家处理或后续订单事实刷新。");
        }
        else if (request.BeverageId < 0)
        {
            AddSkipped(result, "送达酒水", "当前订单没有可用的推荐酒水，等待推荐刷新。");
        }
        else
        {
            result.Automation.Stage = "beverage";
            var safetyTarget = BuildRareAutomationTarget(request);
            var beverageResult = TryDeliverOrderBeverage(runtimeOrder, request.BeverageId, request.BeverageName, "稀客订单");
            if (!beverageResult.Ok)
            {
                AddFailure(result, "送达酒水", beverageResult.Message, beverageResult.Code);
                RecordOrderSafetyBarrierIfNeeded(
                    beverageResult.Code,
                    safetyTarget,
                    beverageResult.Message);
                return Finish(result);
            }

            deliveredItemCount++;
            result.ServedBeverage = true;
            result.Steps.Add(new OrderPreparationStep
            {
                Code = OrderPreparationStepCodes.BeverageDelivered,
                Name = "送达酒水",
                Ok = true,
                Message = beverageResult.Message,
            });
        }

        if (!EnsureLifecycleSessionActive(result, sessionGeneration, "送达酒水后")) return Finish(result);

        result.ServedFood = ReadOrderServedFood(runtimeOrder.Order) != null;
        result.ServedBeverage = ReadOrderServedBeverage(runtimeOrder.Order) != null;

        if (!AddPatientRecoveryStepIfNeeded(result, runtimeOrder, deliveredItemCount))
        {
            return Finish(result);
        }

        if (!EnsureLifecycleSessionActive(result, sessionGeneration, "恢复耐心后")) return Finish(result);

        result.Automation.Stage = "order";
        var evaluationTarget = BuildRareAutomationTarget(request);
        if (RequiresNativeWackyKoishiBossEvaluationEntry(request))
        {
            if (!TryEvaluateWackyKoishiBossOrderIfReady(result, request, runtimeOrder, "触发上菜评价", "当前订单", evaluationTarget))
            {
                return Finish(result);
            }

            EnsureLifecycleSessionActive(result, sessionGeneration, "触发评价后");
            return Finish(result);
        }

        if (IsWackyKoishiBossRequest(request))
        {
            AppendWackyBossRuntimeDiagnostic("rare-evaluate-generic", request, runtimeOrder, "call-generic-evaluate", "Koishi boss clue-stage order uses regular order evaluation.");
        }

        if (IsYuyukoBossRequest(request))
        {
            if (!TryEvaluateYuyukoChallengeOrderIfReady(result, request, runtimeOrder, "触发上菜评价", "当前订单", evaluationTarget))
            {
                return Finish(result);
            }

            EnsureLifecycleSessionActive(result, sessionGeneration, "触发评价后");
            return Finish(result);
        }

        if (!TryEvaluateOrderIfReady(result, runtimeOrder, "触发上菜评价", "当前订单", evaluationTarget))
        {
            return Finish(result);
        }

        EnsureLifecycleSessionActive(result, sessionGeneration, "触发评价后");
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
        if (!RuntimeNightBusinessLifecycle.IsActive) return BuildLifecycleUnavailableResult(request, "normal");
        var sessionGeneration = RuntimeNightBusinessLifecycle.Generation;

        var traceId = ResolveRequestTraceId(OrderTraceKind.Normal, request);
        AppendWackyRequestDiagnostic("normal-complete-start", request, traceId, "normal");
        AppendYuyukoRequestDiagnostic("normal-complete-start", request, traceId, "normal");
        var result = new OrderPreparationResult
        {
            Order = new OrderPreparationOrder
            {
                TraceId = traceId,
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

        if (TryApplyUnresolvedAutomationSafetyBarrier(result, "normal", traceId, request.OrderKey))
        {
            return Finish(result);
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "选择普客订单",
            Ok = true,
            Message = FormatNormalSelectionMessage(request, result.Order.GuestName),
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

        if (!TryValidateYuyukoPhase3NormalOrderTargetInvariant(request, runtimeOrder, out var yuyukoNormalTargetDiagnostic))
        {
            AppendYuyukoRuntimeDiagnostic(
                "yuyuko-normal-target-invariant",
                request,
                runtimeOrder,
                "blocked-normal-target",
                yuyukoNormalTargetDiagnostic);
            AddFailure(
                result,
                "校验幽幽子三阶段普客目标",
                "幽幽子三阶段普客订单执行目标未满足原订单料理/酒水，已停止自动送达和评价，避免触发原生差评。"
                + $"诊断：{yuyukoNormalTargetDiagnostic}。");
            return Finish(result);
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "匹配普客订单",
            Ok = true,
            Message = $"已匹配桌 {request.DeskCode + 1} 的普客订单对象。",
        });

        var expectedFoodId = request.FoodId >= 0 ? request.FoodId : ResolveFoodIdFromRecipeId(request.RecipeId);
        if (!TryReadOrderServedItem(
                runtimeOrder.Order,
                RuntimeDeliveryItemKind.Beverage,
                out var servedBeverage,
                out var servedBeverageDiagnostic))
        {
            result.Automation.Stage = "beverage";
            AddFailure(result, "读取普客订单", $"无法确认订单最终酒水字段，本轮未执行自动化副作用：{servedBeverageDiagnostic}");
            return Finish(result);
        }

        if (!TryReadOrderServedItem(
                runtimeOrder.Order,
                RuntimeDeliveryItemKind.Food,
                out var servedFood,
                out var servedFoodDiagnostic))
        {
            result.Automation.Stage = "cooking-start";
            AddFailure(result, "读取普客订单", $"无法确认订单最终料理字段，本轮未执行自动化副作用：{servedFoodDiagnostic}");
            return Finish(result);
        }

        var foodAlreadyServed = servedFood != null;
        result.ServedFood = foodAlreadyServed;
        result.ServedBeverage = servedBeverage != null;
        var orderAutomationTarget = BuildNormalAutomationTarget(
            request,
            runtimeOrder.Order,
            traceId,
            result.Order.GuestName,
            expectedFoodId);
        var deliveredNormalItemCount = 0;

        if (autoTakeBeverage)
        {
            result.Automation.Stage = "beverage";
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
                if (!TryReadOrderInAirItem(
                        runtimeOrder.Order,
                        RuntimeDeliveryItemKind.Beverage,
                        out var pendingBeverage,
                        out var pendingBeverageDiagnostic))
                {
                    AddFailure(
                        result,
                        "普客送达酒水",
                        $"无法确认订单待送达酒水字段，本轮未执行自动化副作用：{pendingBeverageDiagnostic}");
                    return Finish(result);
                }

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
                            Code = OrderPreparationStepCodes.BeverageDelivered,
                            Name = "普客送达酒水",
                            Ok = true,
                            Message = $"{request.BeverageName} 已处于订单待送达状态，已按游戏送达流程提交。",
                        });
                    }
                    else
                    {
                        AddFailure(result, "普客送达酒水", delivery.Message, delivery.Code);
                        RecordOrderSafetyBarrierIfNeeded(
                            delivery.Code,
                            orderAutomationTarget,
                            delivery.Message);
                        if (request.StopOnError || IsAutomationSafetyBarrierCode(delivery.Code)) return Finish(result);
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
                            Code = OrderPreparationStepCodes.BeverageDelivered,
                            Name = "普客送达酒水",
                            Ok = true,
                            Message = beverageResult.Message,
                        });
                    }
                    else
                    {
                        AddFailure(result, "普客送达酒水", beverageResult.Message, beverageResult.Code);
                        RecordOrderSafetyBarrierIfNeeded(
                            beverageResult.Code,
                            orderAutomationTarget,
                            beverageResult.Message);
                        if (request.StopOnError || IsAutomationSafetyBarrierCode(beverageResult.Code)) return Finish(result);
                    }
                }
            }
        }
        else
        {
            AddSkipped(result, "普客送达酒水", "设置已关闭。");
        }

        if (!EnsureLifecycleSessionActive(result, sessionGeneration, "普客送达酒水后")) return Finish(result);

        if (foodAlreadyServed)
        {
            AddSkipped(result, "普客料理", "该订单已经送达料理，不再自动处理。");
        }
        else if (expectedFoodId < 0)
        {
            result.Automation.Stage = "cooking-start";
            AddFailure(result, "普客料理", "订单没有有效的料理 ID。");
            if (request.StopOnError) return Finish(result);
        }
        else
        {
            if (!TryReadOrderInAirItem(
                    runtimeOrder.Order,
                    RuntimeDeliveryItemKind.Food,
                    out var pendingFood,
                    out var pendingFoodDiagnostic))
            {
                result.Automation.Stage = "cooking-delivery";
                AddFailure(
                    result,
                    "普客料理",
                    $"无法确认订单待送达料理字段，本轮未执行自动化副作用：{pendingFoodDiagnostic}");
                return Finish(result);
            }

            if (pendingFood != null && IsSellable(pendingFood, sellableType: 0, id: expectedFoodId))
            {
                if (autoDeliverFood)
                {
                    result.Automation.Stage = "cooking-delivery";
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
                        AddFailure(result, "普客送达料理", delivery.Message, delivery.Code);
                        RecordOrderSafetyBarrierIfNeeded(
                            delivery.Code,
                            orderAutomationTarget,
                            delivery.Message);
                        if (request.StopOnError || IsAutomationSafetyBarrierCode(delivery.Code)) return Finish(result);
                    }
                }
                else
                {
                    AddSkipped(result, "普客料理", $"目标料理 {request.RecipeName} 已处于订单待送达状态，等待玩家在游戏内确认。");
                }
            }
            else if (pendingFood != null)
            {
                result.Automation.Stage = "cooking-delivery";
                AddFailure(result, "普客料理", $"订单已有其他待送达料理，暂不自动制作 {request.RecipeName}。");
                if (request.StopOnError) return Finish(result);
            }
            else if (HasNormalOrderCookingJob(request.OrderKey, runtimeOrder.Order, request.DeskCode, expectedFoodId, request.BeverageId, out var cookingJobMessage))
            {
                result.Automation.Stage = "cooking-delivery";
                var cookingJobResult = autoDeliverFood
                    ? TryProcessNormalOrderCookingJob(request.OrderKey, runtimeOrder.Order, request.DeskCode, expectedFoodId, request.BeverageId)
                    : (Delivered: false, StepName: "普客开始料理", Message: cookingJobMessage, Code: OrderPreparationStepCodes.CookingPending);
                if (cookingJobResult.Delivered)
                {
                    result.ServedFood = true;
                    result.Steps.Add(new OrderPreparationStep
                    {
                        Code = cookingJobResult.Code,
                        Name = "普客送达料理",
                        Ok = true,
                        Message = cookingJobResult.Message,
                    });
                }
                else if (IsAutomationSafetyBarrierCode(cookingJobResult.Code))
                {
                    AddFailure(
                        result,
                        string.IsNullOrWhiteSpace(cookingJobResult.StepName) ? "普客送达料理" : cookingJobResult.StepName,
                        string.IsNullOrWhiteSpace(cookingJobResult.Message) ? cookingJobMessage : cookingJobResult.Message,
                        cookingJobResult.Code);
                    return Finish(result);
                }
                else if (cookingJobResult.Code == OrderPreparationStepCodes.CookingMismatchStored)
                {
                    AddFailure(
                        result,
                        string.IsNullOrWhiteSpace(cookingJobResult.StepName) ? "普客送达料理" : cookingJobResult.StepName,
                        string.IsNullOrWhiteSpace(cookingJobResult.Message) ? cookingJobMessage : cookingJobResult.Message,
                        cookingJobResult.Code);
                    if (request.StopOnError) return Finish(result);
                }
                else
                {
                    AddSkipped(
                        result,
                        string.IsNullOrWhiteSpace(cookingJobResult.StepName) ? "普客开始料理" : cookingJobResult.StepName,
                        string.IsNullOrWhiteSpace(cookingJobResult.Message) ? cookingJobMessage : cookingJobResult.Message,
                        cookingJobResult.Code);
                }
            }
            else if (request.AutoStartCooking)
            {
                result.Automation.Stage = "cooking-start";
                var recipeId = request.RecipeId >= 0 ? request.RecipeId : ResolveRecipeIdFromFoodId(expectedFoodId);
                if (recipeId < 0)
                {
                    AddFailure(result, "普客开始料理", $"未找到料理 {request.RecipeName}（成品 #{expectedFoodId}）对应的配方 ID。");
                    if (request.StopOnError) return Finish(result);
                }
                else
                {
                    var target = recipeId == orderAutomationTarget.RecipeId
                        ? orderAutomationTarget
                        : BuildNormalAutomationTarget(
                            request,
                            runtimeOrder.Order,
                            traceId,
                            result.Order.GuestName,
                            expectedFoodId,
                            recipeId);
                    var autoDeliverCookedFood = request.AutoCollectCooking && autoDeliverFood;
                    if (TryGetRecentWackyRejectedCookingMessage(target, out var rejectedMessage))
                    {
                        AddSkipped(result, "普客开始料理", rejectedMessage);
                    }
                    else
                    {
                        var cookingResult = TryStartCooking(recipeId, request.RecipeName, request.ExtraIngredientIds, autoDeliverCookedFood, target);
                        if (cookingResult.Ok)
                        {
                            result.Automation.Stage = cookingResult.ExistingJob
                                ? "cooking-delivery"
                                : "cooking-start";
                            result.Automation.JobId = cookingResult.JobId;
                            result.Steps.Add(new OrderPreparationStep
                            {
                                Code = cookingResult.ExistingJob
                                    ? OrderPreparationStepCodes.CookingPending
                                    : OrderPreparationStepCodes.CookingStarted,
                                Name = "普客开始料理",
                                Ok = true,
                                Skipped = cookingResult.ExistingJob,
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
                            AddFailure(result, "普客开始料理", cookingResult.Message, cookingResult.Code);
                            if (request.StopOnError || IsAutomationSafetyBarrierCode(cookingResult.Code)) return Finish(result);
                        }
                    }
                }
            }
            else
            {
                AddSkipped(result, "普客料理", $"普客订单尚未获得目标料理 {request.RecipeName}（料理 #{expectedFoodId}），自动制作料理已关闭。");
            }
        }

        if (!EnsureLifecycleSessionActive(result, sessionGeneration, "普客料理处理后")) return Finish(result);

        result.ServedFood = ReadOrderServedFood(runtimeOrder.Order) != null;
        result.ServedBeverage = ReadOrderServedBeverage(runtimeOrder.Order) != null;
        if (!AddPatientRecoveryStepIfNeeded(result, runtimeOrder, deliveredNormalItemCount))
        {
            return Finish(result);
        }

        if (!EnsureLifecycleSessionActive(result, sessionGeneration, "普客恢复耐心后")) return Finish(result);

        if (autoCompleteOrder)
        {
            result.Automation.Stage = "order";
        }

        if (autoCompleteOrder && RequiresNativeWackyKoishiBossEvaluationEntry(request))
        {
            if (!TryEvaluateWackyKoishiBossOrderIfReady(result, request, runtimeOrder, "触发普客评价", "当前普客订单", orderAutomationTarget))
            {
                return Finish(result);
            }
        }
        else if (autoCompleteOrder)
        {
            if (IsWackyKoishiBossRequest(request))
            {
                AppendWackyBossRuntimeDiagnostic("normal-evaluate-generic", request, runtimeOrder, "call-generic-evaluate", "Koishi boss clue-stage order uses regular order evaluation.");
            }

            if (IsYuyukoBossRequest(request))
            {
                if (!TryEvaluateYuyukoChallengeOrderIfReady(result, request, runtimeOrder, "触发普客评价", "当前普客订单", orderAutomationTarget, reacquireLiveOrder: false))
                {
                    return Finish(result);
                }
            }
            else if (!TryEvaluateOrderIfReady(result, runtimeOrder, "触发普客评价", "当前普客订单", orderAutomationTarget))
            {
                return Finish(result);
            }
        }
        else
        {
            AddSkipped(result, "触发普客评价", "设置已关闭。");
        }

        EnsureLifecycleSessionActive(result, sessionGeneration, "普客触发评价后");
        return Finish(result);
    }

    /// <summary>
    /// 轮询自动料理 job，并在本锅料理完成时直接送达目标订单。
    /// </summary>
    /// <returns>本轮产生的用户可见自动化消息。</returns>
    /// <remarks>
    /// 该方法由 Overlay 的 Update 循环调用，必须保持轻量且容忍游戏对象临时不可用。
    /// job 的锅次、成品可读性、制作进展和送达等待均有明确边界，终态只释放 Mod 所有权。
    /// </remarks>
    public static AutomationCookingProcessResult ProcessAutomationCookingJobs(bool timeoutEligible = true)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive)
        {
            return new AutomationCookingProcessResult(Array.Empty<string>(), false);
        }

        var sessionGeneration = RuntimeNightBusinessLifecycle.Generation;
        var messages = new List<string>();
        var changed = false;
        lock (AutomationCookingJobLock)
        {
            for (var i = AutomationCookingJobs.Count - 1; i >= 0; i--)
            {
                var job = AutomationCookingJobs[i];
                var previousState = job.Tracker.State;
                var previousOutcome = job.Tracker.Outcome;
                var previousReason = job.Tracker.ReasonCode;
                var previousProgressBucket = ToProgressBucket(job.Tracker.LastProgress);
                var previousRetrySignature = job.BuildRetrySignature();
                (bool Remove, string Message, string Code) result;
                try
                {
                    result = TryProcessAutomationCookingJob(job, timeoutEligible);
                }
                catch (Exception ex)
                {
                    var message = $"{job.RecipeName} 自动料理任务发生未处理异常，已释放 Mod 所有权并保留厨具当前状态：{ex.GetBaseException().Message}";
                    RecordAutomationRuntimeEvent(
                        OrderPreparationStepCodes.CookingResultUnreadable,
                        job,
                        message,
                        outcome: "blocked",
                        reasonCode: "cooking-job-exception",
                        terminal: true);
                    result = (true, message, OrderPreparationStepCodes.CookingResultUnreadable);
                }

                var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
                if (!lifecycle.IsActive
                    || lifecycle.Generation != sessionGeneration
                    || i >= AutomationCookingJobs.Count
                    || !ReferenceEquals(AutomationCookingJobs[i], job))
                {
                    changed = true;
                    break;
                }

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    messages.Add(result.Message);
                    AppendAutomationLog("job", job.Target, job.FormatLogContext(result.Message));
                }

                if (result.Remove)
                {
                    AppendAutomationLog("job-remove", job.Target, job.FormatLogContext($"age={(DateTime.UtcNow - job.CreatedAtUtc).TotalSeconds:F1}s; code={result.Code}"));
                    AutomationCookingJobs.RemoveAt(i);
                    changed = true;
                }
                else if (!string.Equals(previousState, job.Tracker.State, StringComparison.Ordinal)
                         || !string.Equals(previousOutcome, job.Tracker.Outcome, StringComparison.Ordinal)
                         || !string.Equals(previousReason, job.Tracker.ReasonCode, StringComparison.Ordinal)
                         || previousProgressBucket != ToProgressBucket(job.Tracker.LastProgress)
                         || !string.Equals(previousRetrySignature, job.BuildRetrySignature(), StringComparison.Ordinal))
                {
                    changed = true;
                }
            }
        }

        return new AutomationCookingProcessResult(messages, changed);
    }

    private static int ToProgressBucket(float progress)
    {
        return progress < 0f ? -1 : Math.Clamp((int)Math.Floor(progress * 10f), 0, 10);
    }

    private static string FormatNormalSelectionMessage(OrderPreparationRequest request, string guestName)
    {
        var matchFoodId = request.MatchFoodId >= 0 ? request.MatchFoodId : request.FoodId;
        var matchBeverageId = request.MatchBeverageId >= 0 ? request.MatchBeverageId : request.BeverageId;
        var targetChanged = (matchFoodId >= 0 && request.FoodId >= 0 && matchFoodId != request.FoodId)
            || (matchBeverageId >= 0 && request.BeverageId >= 0 && matchBeverageId != request.BeverageId)
            || request.ExtraIngredientIds.Count > 0;
        var message = $"桌 {request.DeskCode + 1} · {guestName} · 料理 {request.RecipeName}";
        if (!targetChanged && string.IsNullOrWhiteSpace(request.ExecutionMode) && string.IsNullOrWhiteSpace(request.ExecutionReason)) return message;

        var details = new List<string>();
        if (targetChanged)
        {
            details.Add($"原订单 料理 #{matchFoodId} / 酒水 #{matchBeverageId}");
            details.Add($"执行 料理 #{request.FoodId} / 酒水 #{request.BeverageId}");
            if (request.ExtraIngredientIds.Count > 0)
            {
                details.Add($"加料 #{string.Join(",#", request.ExtraIngredientIds)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ExecutionMode))
        {
            details.Add($"执行模式 {request.ExecutionMode.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(request.ExecutionReason))
        {
            details.Add(request.ExecutionReason.Trim());
        }

        return $"{message}（{string.Join("；", details)}）";
    }

    /// <summary>
    /// 取消所有自动料理 job 的 Mod 所有权，不改动厨具、成品或库存。
    /// </summary>
    /// <returns>被取消的 job 数量。</returns>
    public static int ClearAutomationCookingJobs(string reasonCode = "scene-ended")
    {
        lock (AutomationCookingJobLock)
        {
            var count = AutomationCookingJobs.Count;
            foreach (var job in AutomationCookingJobs)
            {
                RecordAutomationRuntimeEvent(
                    OrderPreparationStepCodes.CookingCancelled,
                    job,
                    $"{job.RecipeName} 自动料理任务已取消；厨具和成品保持原状。",
                    outcome: "cancelled",
                    reasonCode: reasonCode,
                    terminal: true);
            }
            AutomationCookingJobs.Clear();
            return count;
        }
    }

    private static OrderPreparationResult BuildLifecycleUnavailableResult(OrderPreparationRequest request, string targetKind)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var message = $"夜间经营运行时不可用（阶段 {lifecycle.Phase}，会话 {lifecycle.Generation}），未执行任何游戏操作。";
        var result = new OrderPreparationResult
        {
            Ok = false,
            Error = message,
            Order = new OrderPreparationOrder
            {
                TraceId = request.TraceId,
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
        result.Automation.Outcome = "cancelled";
        result.Automation.Stage = "runtime";
        result.Automation.ReasonCode = "night-business-lifecycle-unavailable";
        result.Steps.Add(new OrderPreparationStep
        {
            Code = OrderPreparationStepCodes.NightBusinessLifecycleUnavailable,
            Name = targetKind == "normal" ? "普客经营会话检查" : "稀客经营会话检查",
            Ok = false,
            Message = message,
        });
        return result;
    }

    private static bool EnsureLifecycleSessionActive(
        OrderPreparationResult result,
        long expectedGeneration,
        string checkpoint)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (lifecycle.IsActive && lifecycle.Generation == expectedGeneration) return true;

        var message = $"夜间经营会话在{checkpoint}进入 {lifecycle.Phase}；已停止后续游戏对象读写。";
        AddFailure(
            result,
            "经营会话检查",
            message,
            OrderPreparationStepCodes.NightBusinessLifecycleUnavailable);
        result.Error = message;
        return false;
    }

    private static void AppendAutomationLog(string action, CookingCollectionTarget? target, string message)
    {
        AggregateModLogService.AppendAutomation(action, target?.ToLogContext(), message);
    }

    private static string ResolveRequestTraceId(OrderTraceKind kind, OrderPreparationRequest request)
    {
        var stableKey = kind == OrderTraceKind.Normal
            ? string.IsNullOrWhiteSpace(request.OrderKey)
                ? $"normal:{request.DeskCode}|{request.GuestName}|{(request.MatchFoodId >= 0 ? request.MatchFoodId : request.FoodId)}|{(request.MatchBeverageId >= 0 ? request.MatchBeverageId : request.BeverageId)}"
                : $"normal:{request.OrderKey}"
            : BuildRareOrderStableKey(request);
        return RuntimeOrderTraceIdService.GetRequestTraceId(kind, request.TraceId, stableKey);
    }

    private static string BuildRareOrderStableKey(OrderPreparationRequest request)
    {
        var foodTagId = request.FoodTagId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var beverageTagId = request.BeverageTagId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var guestIdentity = request.RuntimeGuestId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        return $"rare:{request.DeskCode}|{guestIdentity}|{foodTagId}|{beverageTagId}";
    }

    private static CookingCollectionTarget BuildRareAutomationTarget(
        OrderPreparationRequest request,
        int? expectedFoodId = null)
    {
        var foodId = expectedFoodId ?? request.FoodId;
        return CookingCollectionTarget.ForRareOrder(request, foodId);
    }

    private static CookingCollectionTarget BuildNormalAutomationTarget(
        OrderPreparationRequest request,
        object? order,
        string traceId,
        string guestName,
        int expectedFoodId,
        int? resolvedRecipeId = null)
    {
        var recipeId = resolvedRecipeId ?? request.RecipeId;
        return CookingCollectionTarget.ForNormalOrder(
            order,
            request.OrderKey,
            traceId,
            request.MatchFoodId,
            request.MatchBeverageId,
            expectedFoodId,
            request.RecipeName,
            request.DeskCode,
            guestName,
            request.BeverageId,
            request.BeverageName,
            recipeId,
            request.ExtraIngredientIds,
            request.PredictedFoodTags,
            request.WackyTargetFoodTags,
            request.SpecialBusinessRole,
            request.ExecutionMode,
            request.ExecutionReason,
            request.AutoCompleteOrder);
    }

    private static void RecordOrderSafetyBarrierIfNeeded(
        string code,
        CookingCollectionTarget target,
        string message)
    {
        if (code is not (OrderPreparationStepCodes.BeverageDeliveryCommitUncertain
            or OrderPreparationStepCodes.FoodDeliveryCommitUncertain
            or OrderPreparationStepCodes.OrderEvaluationCommitUncertain))
        {
            return;
        }

        RecordAutomationRuntimeEvent(
            code,
            target,
            message,
            outcome: "blocked",
            reasonCode: code,
            terminal: true);
    }

    private static bool TryApplyUnresolvedAutomationSafetyBarrier(
        OrderPreparationResult result,
        string targetKind,
        string traceId,
        string orderKey)
    {
        var targetIdentity = BuildAutomationSafetyTargetIdentity(targetKind, traceId, orderKey);
        AutomationSafetyBarrierRecord? barrier;
        lock (AutomationCookingJobLock)
        {
            UnresolvedAutomationSafetyBarriers.TryGetLatest(targetIdentity, out barrier);
        }

        if (barrier == null) return false;
        result.Automation.Stage = barrier.Stage;
        AddFailure(
            result,
            "自动化安全栅栏",
            $"该订单仍有未人工确认的游戏副作用（事件 #{barrier.Sequence}）：{barrier.Message} 请检查游戏状态并在伴随窗口点击“确认已处理”；Mod 确认 ACK 前不会再次执行该订单。",
            barrier.Code);
        return true;
    }

    private static bool TryGetUnresolvedAutomationSafetyBarrier(
        CookingCollectionTarget target,
        out AutomationSafetyBarrierRecord? barrier)
    {
        var targetKind = target.Kind == CookingCollectionTargetKind.RareOrder ? "rare" : "normal";
        var targetIdentity = BuildAutomationSafetyTargetIdentity(targetKind, target.TraceId, target.OrderKey);
        lock (AutomationCookingJobLock)
        {
            return UnresolvedAutomationSafetyBarriers.TryGetLatest(targetIdentity, out barrier);
        }
    }

    private static string BuildAutomationSafetyTargetIdentity(string targetKind, string traceId, string orderKey)
    {
        if (string.Equals(targetKind, "normal", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(orderKey))
        {
            return $"normal:order:{orderKey.Trim()}";
        }

        return $"{targetKind}:trace:{traceId.Trim()}";
    }

    private static void RecordAutomationRuntimeEvent(
        string code,
        CookingCollectionTarget target,
        string message,
        int actualFoodId = -1,
        IReadOnlyList<string>? targetFoodTags = null,
        IReadOnlyList<string>? actualFoodTags = null,
        AutomationCookingJob? job = null,
        string outcome = "",
        string reasonCode = "",
        bool terminal = false)
    {
        if (string.IsNullOrWhiteSpace(code)) return;

        lock (AutomationCookingJobLock)
        {
            AutomationRuntimeEventSequence++;
            var runtimeEvent = new AutomationRuntimeEvent
            {
                Sequence = AutomationRuntimeEventSequence,
                CreatedAtUtc = DateTime.UtcNow,
                Code = code,
                JobId = job?.JobId ?? "",
                Outcome = outcome,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? code : reasonCode,
                Terminal = terminal,
                Generation = job?.Generation ?? 0,
                CookerPhase = job?.Tracker.LastPhase ?? -1,
                CookerProgress = job?.Tracker.LastProgress ?? -1f,
                TargetKind = target.Kind == CookingCollectionTargetKind.RareOrder ? "rare" : "normal",
                TraceId = target.TraceId,
                OrderKey = target.OrderKey,
                DeskCode = target.DeskCode,
                GuestId = target.GuestId,
                GuestName = target.GuestName,
                FoodId = target.FoodId,
                FoodName = target.FoodName,
                BeverageId = target.BeverageId,
                BeverageName = target.BeverageName,
                RecipeId = target.RecipeId,
                ExtraIngredientIds = target.ExtraIngredientIds.ToList(),
                ActualFoodId = actualFoodId,
                TargetFoodTags = (targetFoodTags ?? Array.Empty<string>()).ToList(),
                ActualFoodTags = (actualFoodTags ?? Array.Empty<string>()).ToList(),
                Message = message,
            };
            AutomationRuntimeEvents.Add(runtimeEvent);

            if (terminal && string.Equals(outcome, "blocked", StringComparison.Ordinal)
                && IsAutomationSafetyBarrierCode(code))
            {
                var targetKind = target.Kind == CookingCollectionTargetKind.RareOrder ? "rare" : "normal";
                UnresolvedAutomationSafetyBarriers.Register(new AutomationSafetyBarrierRecord(
                    runtimeEvent.Sequence,
                    BuildAutomationSafetyTargetIdentity(targetKind, target.TraceId, target.OrderKey),
                    code,
                    code switch
                    {
                        OrderPreparationStepCodes.BeverageDeliveryCommitUncertain => "beverage",
                        OrderPreparationStepCodes.CookingStartUnowned => "cooking-start",
                        OrderPreparationStepCodes.OrderEvaluationCommitUncertain => "order",
                        _ => "cooking-delivery",
                    },
                    message));
            }

            while (AutomationRuntimeEvents.Count > MaxAutomationRuntimeEvents)
            {
                var removableIndex = AutomationRuntimeEvents.FindIndex(candidate =>
                    !UnresolvedAutomationSafetyBarriers.Contains(candidate.Sequence));
                if (removableIndex < 0) break;
                AutomationRuntimeEvents.RemoveAt(removableIndex);
            }
        }
    }

    private static void RecordAutomationRuntimeEvent(
        string code,
        AutomationCookingJob job,
        string message,
        int actualFoodId = -1,
        IReadOnlyList<string>? targetFoodTags = null,
        IReadOnlyList<string>? actualFoodTags = null,
        string outcome = "",
        string reasonCode = "",
        bool terminal = false)
    {
        RecordAutomationRuntimeEvent(
            code,
            job.Target,
            message,
            actualFoodId,
            targetFoodTags,
            actualFoodTags,
            job,
            outcome,
            reasonCode,
            terminal);
    }

    private static OrderPreparationResult Fail(OrderPreparationResult result, string error)
    {
        result.Error = error;
        result.Ok = false;
        result.Prepared = false;
        result.Automation.Outcome = "fatal";
        result.Automation.Stage = "validation";
        result.Automation.ReasonCode = "request-validation-failed";
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

        ApplyStructuredAutomationOutcome(result);

        return result;
    }

    private static void ApplyStructuredAutomationOutcome(OrderPreparationResult result)
    {
        var codes = result.Steps
            .Select(step => step.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.Ordinal);
        var blockedCode = result.Steps
            .Select(step => step.Code)
            .FirstOrDefault(IsAutomationSafetyBarrierCode);
        if (!string.IsNullOrWhiteSpace(blockedCode))
        {
            result.Automation.Outcome = "blocked";
            result.Automation.Stage = blockedCode switch
            {
                OrderPreparationStepCodes.BeverageDeliveryCommitUncertain => "beverage",
                OrderPreparationStepCodes.CookingStartUnowned => "cooking-start",
                OrderPreparationStepCodes.OrderEvaluationCommitUncertain => "order",
                _ => "cooking-delivery",
            };
            result.Automation.ReasonCode = blockedCode;
            return;
        }

        if (codes.Contains(OrderPreparationStepCodes.NightBusinessLifecycleUnavailable))
        {
            result.Automation.Outcome = "cancelled";
            result.Automation.Stage = "runtime";
            result.Automation.ReasonCode = OrderPreparationStepCodes.NightBusinessLifecycleUnavailable;
            result.Automation.RetryAfterMs = 0;
            return;
        }

        var interruptedCode = new[]
        {
            OrderPreparationStepCodes.CookingResultRemoved,
            OrderPreparationStepCodes.CookingControllerReused,
            OrderPreparationStepCodes.CookingMismatchStored,
            OrderPreparationStepCodes.CookingTargetUnavailableStored,
        }.FirstOrDefault(codes.Contains);
        if (!string.IsNullOrWhiteSpace(interruptedCode))
        {
            result.Automation.Outcome = "interrupted";
            result.Automation.Stage = "cooking-delivery";
            result.Automation.ReasonCode = interruptedCode;
            result.Automation.RetryAfterMs = 500;
            return;
        }

        if (result.CompletedOrder)
        {
            result.Automation.Outcome = "completed";
            result.Automation.Stage = "order";
            result.Automation.ReasonCode = "order-completed";
            return;
        }

        var completedCookingCode = codes.Contains(OrderPreparationStepCodes.FoodDelivered)
            ? OrderPreparationStepCodes.FoodDelivered
            : codes.Contains(OrderPreparationStepCodes.CookingTargetAlreadyServedStored)
                ? OrderPreparationStepCodes.CookingTargetAlreadyServedStored
                : "";
        if (!string.IsNullOrWhiteSpace(completedCookingCode))
        {
            result.Automation.Outcome = "completed";
            result.Automation.Stage = "cooking-delivery";
            result.Automation.ReasonCode = completedCookingCode;
            return;
        }

        if (!result.Ok)
        {
            result.Automation.Outcome = "retryable-failure";
            result.Automation.ReasonCode = result.Steps.FirstOrDefault(step => !step.Ok && !step.Skipped)?.Code
                ?? "command-failed";
            if (string.IsNullOrWhiteSpace(result.Automation.ReasonCode)) result.Automation.ReasonCode = "command-failed";
            result.Automation.RetryAfterMs = 1000;
            return;
        }

        if (codes.Contains(OrderPreparationStepCodes.CookingPending))
        {
            result.Automation.Outcome = "waiting";
            result.Automation.Stage = "cooking-delivery";
            result.Automation.ReasonCode = OrderPreparationStepCodes.CookingPending;
            return;
        }

        if (codes.Contains(OrderPreparationStepCodes.CookingStarted)
            || codes.Contains(OrderPreparationStepCodes.BeverageDelivered))
        {
            result.Automation.Outcome = "progressed";
            result.Automation.ReasonCode = codes.Contains(OrderPreparationStepCodes.CookingStarted)
                ? OrderPreparationStepCodes.CookingStarted
                : OrderPreparationStepCodes.BeverageDelivered;
            result.Automation.Stage = codes.Contains(OrderPreparationStepCodes.CookingStarted)
                ? "cooking-start"
                : "beverage";
            return;
        }

        result.Automation.Outcome = "waiting";
        result.Automation.ReasonCode = "no-action-required";
    }

    private static void AddFailure(OrderPreparationResult result, string name, string message, string code = "")
    {
        result.Steps.Add(new OrderPreparationStep
        {
            Code = code,
            Name = name,
            Ok = false,
            Message = message,
        });
    }

    private static bool IsAutomationSafetyBarrierCode(string code)
    {
        return code is OrderPreparationStepCodes.CookingTagsUnreadableStored
            or OrderPreparationStepCodes.BeverageDeliveryCommitUncertain
            or OrderPreparationStepCodes.FoodDeliveryCommitUncertain
            or OrderPreparationStepCodes.CookingResultUnreadable
            or OrderPreparationStepCodes.CookingDeliveryBlocked
            or OrderPreparationStepCodes.CookingStartUnowned
            or OrderPreparationStepCodes.CookingProgressStalled
            or OrderPreparationStepCodes.CookingProgressRegressed
            or OrderPreparationStepCodes.CookingDeliveryCommitUncertain
            or OrderPreparationStepCodes.CookingDeliveryCleanupBlocked
            or OrderPreparationStepCodes.CookingWarmerCommitUncertain
            or OrderPreparationStepCodes.CookingWarmerResetBlocked
            or OrderPreparationStepCodes.CookingManualHandoffUnreadable
            or OrderPreparationStepCodes.OrderEvaluationCommitUncertain;
    }

    private static void AddSkipped(OrderPreparationResult result, string name, string message, string code = "")
    {
        result.Steps.Add(new OrderPreparationStep
        {
            Code = code,
            Name = name,
            Ok = true,
            Skipped = true,
            Message = message,
        });
    }

    private sealed record AutomationWarmerCompletion(
        string Code,
        string Outcome,
        string ReasonCode,
        string MessagePrefix,
        int ActualFoodId,
        IReadOnlyList<string> TargetTags,
        IReadOnlyList<string> ActualTags,
        string DiagnosticEvent = "",
        bool RememberRejectedRecipe = false);

    private sealed record AutomationFoodDeliveryCompletion(
        string Message,
        int ActualFoodId,
        IReadOnlyList<string> TargetTags,
        IReadOnlyList<string> ActualTags);

    private sealed record RuntimeOrderEvaluationResult(
        bool Ok,
        bool Completed,
        bool Skipped,
        string Message,
        string Code = "");


    /// <summary>
    /// 等待料理完成并直接送达的上下文。
    /// </summary>
    private sealed class AutomationCookingJob
    {
        public string JobId { get; init; } = "";
        public object CookController { get; init; } = new();
        public nint ControllerPointer { get; init; }
        public long Generation { get; init; }
        public string RecipeName { get; init; } = "";
        public DateTime CreatedAtUtc { get; init; }
        public CookingCollectionTarget Target { get; init; } = CookingCollectionTarget.ForRareOrder(new OrderPreparationRequest(), -1);
        public AutomationCookingJobTracker Tracker { get; init; } = new(0, DateTime.UtcNow, -1, 0f);
        public bool AutoDeliverFood { get; set; }
        public bool ManualHandoffObserved { get; set; }
        public int ManualHandoffMissingOrderCount { get; set; }
        public int ManualHandoffReadFailureCount { get; set; }
        public AutomationEffectiveTimeoutClock ManualHandoffMissingOrderClock { get; init; } = new(DateTime.UtcNow, initiallyEligible: false);
        public AutomationEffectiveTimeoutClock ManualHandoffReadFailureClock { get; init; } = new(DateTime.UtcNow, initiallyEligible: false);
        public AutomationEffectiveTimeoutClock DeliveryTimeoutClock { get; init; } = new(DateTime.UtcNow, initiallyEligible: false);
        public nint CurrentResultPointer { get; set; }
        public bool WarmerStoreCommitted => WarmerResetTracker.Committed;
        public bool WarmerStoreCommitUncertain => WarmerResetTracker.CommitUncertain;
        public string WarmerStoreStatus { get; set; } = "";
        public object? WarmerStoredFood { get; set; }
        public AutomationWarmerCompletion? WarmerCompletion { get; set; }
        public AutomationBoundedCleanupTracker WarmerResetTracker { get; } = new(MaxCookerCleanupAttempts);
        public int WarmerResetAttempts => WarmerResetTracker.AttemptCount;
        public bool FoodDeliveryCommitted => FoodDeliveryCleanupTracker.Committed;
        public bool FoodDeliveryCommitUncertain => FoodDeliveryCleanupTracker.CommitUncertain;
        public object? DeliveredFood { get; set; }
        public AutomationFoodDeliveryCompletion? FoodDeliveryCompletion { get; set; }
        public AutomationBoundedCleanupTracker FoodDeliveryCleanupTracker { get; } = new(MaxCookerCleanupAttempts);
        public int FoodDeliveryCleanupAttempts => FoodDeliveryCleanupTracker.AttemptCount;
        public int MissingTargetCount { get; private set; }
        public TimeSpan? FirstMissingTargetAtEffectiveElapsed { get; private set; }
        public int MissingControllerCount { get; private set; }
        public TimeSpan? FirstMissingControllerAtEffectiveElapsed { get; private set; }

        public (int Count, TimeSpan EffectiveAge) RecordDeliveryFailure(
            AutomationDeliveryFailureKind kind,
            TimeSpan effectiveElapsed)
        {
            if (kind == AutomationDeliveryFailureKind.MissingController)
            {
                FirstMissingControllerAtEffectiveElapsed ??= effectiveElapsed;
                MissingControllerCount++;
                MissingTargetCount = 0;
                FirstMissingTargetAtEffectiveElapsed = null;
                return (
                    MissingControllerCount,
                    effectiveElapsed - FirstMissingControllerAtEffectiveElapsed.Value);
            }

            FirstMissingTargetAtEffectiveElapsed ??= effectiveElapsed;
            MissingTargetCount++;
            MissingControllerCount = 0;
            FirstMissingControllerAtEffectiveElapsed = null;
            return (
                MissingTargetCount,
                effectiveElapsed - FirstMissingTargetAtEffectiveElapsed.Value);
        }

        public void ResetDeliveryFailures()
        {
            MissingTargetCount = 0;
            FirstMissingTargetAtEffectiveElapsed = null;
            MissingControllerCount = 0;
            FirstMissingControllerAtEffectiveElapsed = null;
        }

        public string FormatLogContext(string detail)
        {
            return $"jobId={JobId}; generation={Generation}; controller=0x{(long)ControllerPointer:X}; result=0x{(long)CurrentResultPointer:X}; phase={Tracker.LastPhase}; progress={Tracker.LastProgress:F3}; {detail}";
        }

        public string BuildRetrySignature()
        {
            return $"{Tracker.OwnershipObservationFailures}:{Tracker.RegressiveObservations}:{AutoDeliverFood}:{ManualHandoffObserved}:"
                + $"{MissingTargetCount + MissingControllerCount}:{WarmerStoreCommitted}:{WarmerStoreCommitUncertain}:"
                + $"{WarmerResetAttempts}:{FoodDeliveryCommitted}:{FoodDeliveryCommitUncertain}:{FoodDeliveryCleanupAttempts}";
        }

        public AutomationCookingJobSnapshot ToSnapshot()
        {
            return new AutomationCookingJobSnapshot
            {
                JobId = JobId,
                TargetKind = Target.Kind == CookingCollectionTargetKind.RareOrder ? "rare" : "normal",
                TraceId = Target.TraceId,
                OrderKey = Target.OrderKey,
                DeskCode = Target.DeskCode,
                GuestId = Target.GuestId,
                GuestName = Target.GuestName,
                FoodId = Target.FoodId,
                FoodName = Target.FoodName,
                RecipeId = Target.RecipeId,
                State = Tracker.State,
                Outcome = Tracker.Outcome,
                ReasonCode = Tracker.ReasonCode,
                AutoDeliverFood = AutoDeliverFood,
                ControllerId = $"0x{(long)ControllerPointer:X}",
                ResultId = CurrentResultPointer == 0 ? "" : $"0x{(long)CurrentResultPointer:X}",
                Generation = Generation,
                CookerPhase = Tracker.LastPhase,
                CookerProgress = Tracker.LastProgress,
                OwnershipObservationFailures = Tracker.OwnershipObservationFailures,
                RegressiveObservations = Tracker.RegressiveObservations,
                DeliveryFailureAttempts = MissingTargetCount + MissingControllerCount,
                ManualHandoffReadFailures = ManualHandoffReadFailureCount,
                WarmerStoreCommitted = WarmerStoreCommitted,
                WarmerStoreCommitUncertain = WarmerStoreCommitUncertain,
                WarmerResetAttempts = WarmerResetAttempts,
                FoodDeliveryCommitted = FoodDeliveryCommitted,
                FoodDeliveryCommitUncertain = FoodDeliveryCommitUncertain,
                FoodDeliveryCleanupAttempts = FoodDeliveryCleanupAttempts,
                StartedAtUtc = Tracker.StartedAtUtc,
                LastObservedAtUtc = Tracker.LastObservedAtUtc,
                LastProgressAtUtc = Tracker.LastProgressAtUtc,
            };
        }
    }

    /// <summary>
    /// 描述自动出锅后的直接送达目标。
    /// </summary>
    private sealed class CookingCollectionTarget
    {
        public CookingCollectionTargetKind Kind { get; private init; }
        public string TraceId { get; private init; } = "";
        public object? Order { get; private init; }
        public string OrderKey { get; private init; } = "";
        public int? GuestId { get; private init; }
        public int? RuntimeGuestId { get; private init; }
        public int? FoodTagId { get; private init; }
        public string FoodTag { get; private init; } = "";
        public int? BeverageTagId { get; private init; }
        public string BeverageTag { get; private init; } = "";
        public int MatchFoodId { get; private init; } = -1;
        public int MatchBeverageId { get; private init; } = -1;
        public int FoodId { get; private init; } = -1;
        public string FoodName { get; private init; } = "";
        public int BeverageId { get; private init; } = -1;
        public string BeverageName { get; private init; } = "";
        public int RecipeId { get; private init; } = -1;
        public IReadOnlyList<int> ExtraIngredientIds { get; private init; } = Array.Empty<int>();
        public IReadOnlyList<string> PredictedFoodTags { get; private init; } = Array.Empty<string>();
        public IReadOnlyList<string> WackyTargetFoodTags { get; private init; } = Array.Empty<string>();
        public string WackyTargetSignature { get; private init; } = "";
        public string SpecialBusinessRole { get; private init; } = "";
        public string ExecutionMode { get; private init; } = "";
        public string ExecutionReason { get; private init; } = "";
        public int DeskCode { get; private init; } = -1;
        public string GuestName { get; private init; } = "";
        public bool AutoCompleteOrder { get; private init; }

        public static CookingCollectionTarget ForRareOrder(OrderPreparationRequest request, int foodId)
        {
            var shouldFallbackToCurrentWackyTarget = !string.Equals(
                request.SpecialBusinessRole,
                WackyCookingCompetitionRuntimePolicy.KoishiBossRole,
                StringComparison.Ordinal);
            var wackyTargetSignature = CaptureRequestedWackyTargetSignature(
                request.WackyTargetFoodTags,
                fallbackToCurrent: shouldFallbackToCurrentWackyTarget,
                out var wackyTargetFoodTags);
            return new CookingCollectionTarget
            {
                Kind = CookingCollectionTargetKind.RareOrder,
                TraceId = RuntimeOrderTraceIdService.GetRequestTraceId(
                    OrderTraceKind.Rare,
                    request.TraceId,
                    BuildRareOrderStableKey(request)),
                GuestId = request.GuestId,
                RuntimeGuestId = request.RuntimeGuestId,
                FoodTagId = request.FoodTagId,
                FoodTag = request.FoodTag,
                BeverageTagId = request.BeverageTagId,
                BeverageTag = request.BeverageTag,
                FoodId = foodId,
                FoodName = request.RecipeName,
                BeverageId = request.BeverageId,
                BeverageName = request.BeverageName,
                RecipeId = request.RecipeId,
                ExtraIngredientIds = request.ExtraIngredientIds.ToList(),
                PredictedFoodTags = WackyCookingCompetitionRuntimePolicy.NormalizeTags(request.PredictedFoodTags).ToList(),
                WackyTargetFoodTags = wackyTargetFoodTags.ToList(),
                WackyTargetSignature = wackyTargetSignature,
                SpecialBusinessRole = request.SpecialBusinessRole,
                ExecutionMode = request.ExecutionMode,
                ExecutionReason = request.ExecutionReason,
                DeskCode = request.DeskCode,
                GuestName = request.GuestName,
            };
        }

        public static CookingCollectionTarget ForNormalOrder(
            object? order,
            string orderKey,
            string traceId,
            int matchFoodId,
            int matchBeverageId,
            int foodId,
            string foodName,
            int deskCode,
            string guestName,
            int beverageId,
            string beverageName,
            int recipeId,
            IReadOnlyList<int> extraIngredientIds,
            IReadOnlyList<string> predictedFoodTags,
            IReadOnlyList<string> wackyTargetFoodTags,
            string specialBusinessRole,
            string executionMode,
            string executionReason,
            bool autoCompleteOrder)
        {
            var normalizedMatchFoodId = matchFoodId >= 0 ? matchFoodId : foodId;
            var normalizedMatchBeverageId = matchBeverageId >= 0 ? matchBeverageId : beverageId;
            var wackyTargetSignature = CaptureRequestedWackyTargetSignature(wackyTargetFoodTags, fallbackToCurrent: false, out var normalizedWackyTargetFoodTags);
            return new CookingCollectionTarget
            {
                Kind = CookingCollectionTargetKind.NormalOrder,
                TraceId = RuntimeOrderTraceIdService.GetRequestTraceId(
                    OrderTraceKind.Normal,
                    traceId,
                    string.IsNullOrWhiteSpace(orderKey)
                        ? $"normal:{deskCode}|{guestName}|{normalizedMatchFoodId}|{normalizedMatchBeverageId}"
                        : $"normal:{orderKey}"),
                Order = order,
                OrderKey = orderKey,
                MatchFoodId = normalizedMatchFoodId,
                MatchBeverageId = normalizedMatchBeverageId,
                FoodId = foodId,
                FoodName = foodName,
                BeverageId = beverageId,
                BeverageName = beverageName,
                RecipeId = recipeId,
                ExtraIngredientIds = extraIngredientIds.ToList(),
                PredictedFoodTags = WackyCookingCompetitionRuntimePolicy.NormalizeTags(predictedFoodTags).ToList(),
                WackyTargetFoodTags = normalizedWackyTargetFoodTags.ToList(),
                WackyTargetSignature = wackyTargetSignature,
                SpecialBusinessRole = specialBusinessRole,
                ExecutionMode = executionMode,
                ExecutionReason = executionReason,
                DeskCode = deskCode,
                GuestName = guestName,
                AutoCompleteOrder = autoCompleteOrder,
            };
        }

        public OrderLogContext ToLogContext()
        {
            return new OrderLogContext
            {
                TraceId = TraceId,
                Kind = Kind == CookingCollectionTargetKind.NormalOrder ? "normal" : "rare",
                OrderKey = OrderKey,
                DeskCode = DeskCode,
                GuestId = GuestId,
                GuestName = GuestName,
                MatchFoodId = MatchFoodId,
                MatchBeverageId = MatchBeverageId,
                FoodId = FoodId,
                FoodName = FoodName,
                BeverageId = BeverageId,
                BeverageName = BeverageName,
                RuleReason = ExecutionReason,
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
        public bool ExistingJob { get; private init; }
        public string JobId { get; private init; } = "";
        public string Code { get; private init; } = "";

        public static CookingStartResult Succeeded(
            string message,
            string qteMessage,
            bool qteSkipped,
            bool existingJob = false,
            string jobId = "")
        {
            return new CookingStartResult
            {
                Ok = true,
                Message = message,
                QteMessage = qteMessage,
                QteSkipped = qteSkipped,
                ExistingJob = existingJob,
                JobId = jobId,
            };
        }

        public static CookingStartResult Failed(string message, string code = "")
        {
            return new CookingStartResult
            {
                Ok = false,
                Message = message,
                Code = code,
            };
        }
    }

}
