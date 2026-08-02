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
    /// 普客订单有时只暴露成品料理 ID，因此遍历游戏配方表查找精确匹配。
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
            return -1;
        }

        return -1;
    }

    /// <summary>
    /// 调用游戏烹饪系统开始制作料理。
    /// </summary>
    /// <param name="recipeId">目标配方 ID。</param>
    /// <param name="recipeName">用于用户提示和自动化日志的料理名称。</param>
    /// <param name="extraIngredientIds">推荐算法选择的额外加料材料 ID。</param>
    /// <param name="autoCollect">料理完成后是否由 job 直接送达；关闭时 job 只保留手动交接回执。</param>
    /// <param name="cookerControllerIndex">前端根据同轮运行时快照预约的精确厨具控制器索引。</param>
    /// <param name="cookerControllerIdentity">同一快照中的厨具控制器原生身份。</param>
    /// <param name="cookerGridX">同一快照中的厨具网格 X 坐标。</param>
    /// <param name="cookerGridY">同一快照中的厨具网格 Y 坐标。</param>
    /// <param name="cookerGridZ">同一快照中的厨具网格 Z 坐标。</param>
    /// <param name="collectionTarget">料理完成后的直接送达目标；未指定时仅按料理名称登记稀客目标。</param>
    /// <returns>开火结果以及原生 QTE 处理状态。</returns>
    /// <remarks>
    /// 此方法会扣除材料库存、写入厨具控制器、触发游戏开火回调，并始终登记精确锅次 job 防止响应丢失后重复开锅。
    /// 调用前必须已位于夜晚经营场景，且应运行在 Unity 主线程。
    /// </remarks>
    private static CookingStartResult TryStartCooking(
        int recipeId,
        string recipeName,
        IReadOnlyList<int> extraIngredientIds,
        bool autoCollect,
        bool autoCompleteOrder,
        int cookerControllerIndex,
        string cookerControllerIdentity,
        int? cookerGridX,
        int? cookerGridY,
        int? cookerGridZ,
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
        if (TryFindAutomationCookingJob(
                target,
                autoCollect,
                autoCompleteOrder,
                out var existingJob,
                out var existingJobMessage))
        {
            return CookingStartResult.Succeeded(
                existingJobMessage,
                "",
                true,
                existingJob: true,
                jobId: existingJob?.JobId ?? "");
        }

        YuumaCookerTopologyLease? yuumaTopologyLease = null;
        if (IsYuumaBossTarget(target)
            && !YuumaCookerTopologyObserver.TryAcquireFreshLease(
                out yuumaTopologyLease,
                out var topologyLeaseDiagnostic))
        {
            var message = "血池地狱厨具拓扑暂不可完整确认，自动化不会扣料或开锅："
                + topologyLeaseDiagnostic;
            AppendAutomationLog("start-waiting", collectionTarget, $"{recipeName}: {message}");
            return CookingStartResult.WaitForCooker(message);
        }

        if (!RuntimeCookerReservation.TryCreate(
                cookerControllerIndex,
                cookerControllerIdentity,
                cookerGridX,
                cookerGridY,
                cookerGridZ,
                out var cookerReservation,
                out var reservationError))
        {
            var message = $"本次开锅请求缺少完整的厨具预约身份，自动化将等待最新快照重新调度"
                + $"（{reservationError}）。";
            AppendAutomationLog("start-waiting", collectionTarget, $"{recipeName}: {message}");
            return CookingStartResult.WaitForCooker(message);
        }

        var recipeCookerType = ToInt(ReadMember(recipe, "cookerType"), -1);
        var cookerSelection = TryGetCookerFromCookSystem(
            recipeCookerType,
            cookerReservation);
        if (!cookerSelection.Ok
            || cookerSelection.CookController == null
            || cookerSelection.ControllerState == null)
        {
            AppendAutomationLog(
                cookerSelection.Waiting ? "start-waiting" : "start-failed",
                collectionTarget,
                $"{recipeName}: {cookerSelection.Message}");
            return cookerSelection.Waiting
                ? CookingStartResult.WaitForCooker(cookerSelection.Message)
                : CookingStartResult.Failed(cookerSelection.Message);
        }

        var cookController = cookerSelection.CookController;
        var cooker = cookerSelection.ControllerState.Cooker;

        var baseIngredientIds = ReadRecipeIngredientIds(recipe);
        if (baseIngredientIds.Length + extraIngredientIds.Count > MaxFoodIngredientCount)
        {
            AppendAutomationLog("start-failed", collectionTarget, $"{recipeName}: too many ingredients base={baseIngredientIds.Length}; extra={extraIngredientIds.Count}");
            return CookingStartResult.Failed($"料理材料超过游戏上限：基础 {baseIngredientIds.Length} 个，加料 {extraIngredientIds.Count} 个，最多 {MaxFoodIngredientCount} 个。");
        }

        object? cookResult;
        try
        {
            cookResult = CreateCookResult(recipe, extraIngredientIds, cooker);
        }
        catch (Exception ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            AppendAutomationLog("start-failed", collectionTarget, $"{recipeName}: MatchedCookCombo failed: {message}");
            return CookingStartResult.Failed($"无法生成料理结果，已取消开火：{message}");
        }

        if (cookResult == null && extraIngredientIds.Count > 0)
        {
            AppendAutomationLog("start-failed", collectionTarget, $"{recipeName}: failed to create MatchedCookCombo for {extraIngredientIds.Count} extra ingredients");
            return CookingStartResult.Failed($"无法根据 {extraIngredientIds.Count} 个额外食材生成料理结果，已取消开火。");
        }

        var finalFood = cookResult ?? baseFood;
        if (!RuntimeCookingGenerationTracker.EnsureAttached())
        {
            return CookingStartResult.Failed($"自动料理锅次追踪不可用，已在扣除材料前取消开火：{RuntimeCookingGenerationTracker.Status}");
        }

        if (!TryReadNativeObjectPointer(cookController, out var controllerPointer))
        {
            return CookingStartResult.Failed("无法读取厨具原生身份，已在扣除材料前取消自动开火。");
        }

        if (!TryReadNativeObjectPointer(recipe, out var recipePointer))
        {
            return CookingStartResult.Failed("无法读取配方原生身份，已在扣除材料前取消自动开火。");
        }

        if (!TryRevalidateCookerBeforeStart(
                cookerReservation,
                cookController,
                cooker,
                recipeCookerType,
                out var cookerWaiting,
                out var cookerValidationMessage))
        {
            AppendAutomationLog(
                cookerWaiting ? "start-waiting" : "start-failed",
                collectionTarget,
                $"{recipeName}: {cookerValidationMessage}");
            return cookerWaiting
                ? CookingStartResult.WaitForCooker(cookerValidationMessage)
                : CookingStartResult.Failed(cookerValidationMessage);
        }

        var ingredientIds = baseIngredientIds.Concat(extraIngredientIds).ToArray();
        if (!HasEnoughIngredients(ingredientIds, out var missingIngredientId))
        {
            AppendAutomationLog("start-failed", collectionTarget, $"{recipeName}: missing ingredient #{missingIngredientId}");
            return CookingStartResult.Failed($"材料不足，缺少材料 #{missingIngredientId}。");
        }

        if (!TryCaptureYuumaFoodTargetRevision(
                target,
                out var specialFoodTargetRevision,
                out var specialFoodTargetRevisionError))
        {
            AppendAutomationLog(
                "start-waiting",
                collectionTarget,
                $"{recipeName}: {specialFoodTargetRevisionError}");
            return CookingStartResult.Failed(specialFoodTargetRevisionError);
        }

        if (yuumaTopologyLease != null
            && !YuumaCookerTopologyObserver.TryValidateFreshLease(
                yuumaTopologyLease,
                out var preDeductionTopologyDiagnostic))
        {
            var message = "血池地狱厨具拓扑在扣料前发生变化，自动化将等待新快照重新规划："
                + preDeductionTopologyDiagnostic;
            AppendAutomationLog("start-waiting", collectionTarget, $"{recipeName}: {message}");
            return CookingStartResult.WaitForCooker(message);
        }

        if (ingredientIds.Length > 0)
        {
            try
            {
                foreach (var ingredientId in ingredientIds)
                {
                    InvokeRuntimeStorageOut("IngredientOut", ingredientId);
                }
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;
                AppendAutomationLog("start-unowned", collectionTarget, $"{recipeName}: ingredient deduction became uncertain: {message}");
                return BlockCookingStartUnowned(
                    target,
                    $"扣除料理材料时游戏入口执行异常，当前库存结果无法安全确认；为避免重复扣料，自动化已暂停：{message}");
            }
        }

        if (!TryRevalidateCookerBeforeStart(
                cookerReservation,
                cookController,
                cooker,
                recipeCookerType,
                out _,
                out var finalCookerValidationMessage))
        {
            AppendAutomationLog(
                "start-unowned",
                collectionTarget,
                $"{recipeName}: cooker reservation changed after ingredient deduction: "
                + finalCookerValidationMessage);
            return BlockCookingStartUnowned(
                target,
                "材料已扣除，但精确预约的厨具身份、位置或挑战锁定状态在开火前发生变化；"
                + "Mod 未调用 SetCook，自动化已暂停以避免重复扣料："
                + finalCookerValidationMessage);
        }

        if (yuumaTopologyLease != null
            && !YuumaCookerTopologyObserver.TryValidateFreshLease(
                yuumaTopologyLease,
                out var preSetCookTopologyDiagnostic))
        {
            AppendAutomationLog(
                "start-unowned",
                collectionTarget,
                $"{recipeName}: topology changed after ingredient deduction: {preSetCookTopologyDiagnostic}");
            return BlockCookingStartUnowned(
                target,
                "材料已扣除，但血池地狱厨具拓扑在 SetCook 前发生变化；"
                + "Mod 未调用 SetCook，自动化已暂停以避免重复扣料："
                + preSetCookTopologyDiagnostic);
        }

        try
        {
            InvokeInstance(cookController, "SetCook", new object?[] { finalFood, recipe, true });
        }
        catch (Exception ex)
        {
                var message = ex.GetBaseException().Message;
                AppendAutomationLog("start-unowned", collectionTarget, $"{recipeName}: SetCook failed after material deduction: {message}; cooker={DescribeCookController(cookController)}");
                return BlockCookingStartUnowned(
                    target,
                    $"材料已扣除，但游戏开火入口执行异常；为避免重复扣料和重复开锅，自动化已暂停并保留厨具当前状态：{message}");
        }

        if (!RuntimeCookingGenerationTracker.TryGetOwnershipSnapshot(
                cookController,
                out var ownershipSnapshot,
                out var ownershipDiagnostic)
            || ownershipSnapshot.LastMutation != RuntimeCookingContentMutation.SetCook
            || !ownershipSnapshot.MutationCompleted)
        {
            AppendAutomationLog(
                "start-unowned",
                collectionTarget,
                $"{recipeName}: {ownershipDiagnostic}; mutation={ownershipSnapshot.LastMutation}; "
                + $"cooker={DescribeCookController(cookController)}");
            return BlockCookingStartUnowned(
                target,
                $"料理已经写入厨具，但未能立即取得本次 SetCook 的安全所有权，"
                + $"已交还玩家手动处理且不会自动操作该厨具：{ownershipDiagnostic}");
        }

        var qteResult = TryHandleCookingQte();
        if (yuumaTopologyLease != null
            && !YuumaCookerTopologyObserver.TryValidateFreshLease(
                yuumaTopologyLease,
                out var preCountdownTopologyDiagnostic))
        {
            AppendAutomationLog(
                "start-unowned",
                collectionTarget,
                $"{recipeName}: topology changed after SetCook: {preCountdownTopologyDiagnostic}");
            return BlockCookingStartUnowned(
                target,
                "料理已经写入厨具，但血池地狱厨具拓扑在倒计时前发生变化；"
                + "自动化已停止且不会继续访问原厨具："
                + preCountdownTopologyDiagnostic);
        }

        try
        {
            InvokeInstance(cookController, "StartCookCountDown", new object?[] { 1f, false });
        }
        catch (Exception ex)
        {
                var message = ex.GetBaseException().Message;
                AppendAutomationLog(
                    "start-unowned",
                    collectionTarget,
                    $"{recipeName}: StartCookCountDown failed: {message}; generation={ownershipSnapshot.Generation}; "
                    + $"contentRevision={ownershipSnapshot.ContentRevision}; cooker={DescribeCookController(cookController)}");
                return BlockCookingStartUnowned(
                    target,
                    $"料理已经写入厨具，但游戏倒计时入口执行异常；自动化已暂停并保留该厨具供玩家处理：{message}");
        }

        var cookSystem = RuntimeCookerReflection.GetCookSystemManager();
        if (cookSystem != null)
        {
            TryInvokeInstance(cookSystem, "CallCookerStartCallback", new object?[] { finalFood, recipe });
        }

        if (yuumaTopologyLease != null
            && !YuumaCookerTopologyObserver.TryValidateFreshLease(
                yuumaTopologyLease,
                out var postStartTopologyDiagnostic))
        {
            AppendAutomationLog(
                "start-unowned",
                collectionTarget,
                $"{recipeName}: topology changed during native start callbacks: {postStartTopologyDiagnostic}");
            return BlockCookingStartUnowned(
                target,
                "料理已经开火，但血池地狱厨具拓扑在原生开锅回调期间发生变化；"
                + "自动化已停止且不会读取或操作原厨具："
                + postStartTopologyDiagnostic);
        }

        if (!TryValidateCookerStart(cookController, recipe, targetFoodId, out var startDiagnostic))
        {
            AppendAutomationLog("start-failed", collectionTarget, $"{recipeName}: {startDiagnostic}; cooker={DescribeCookController(cookController)}");
            return BlockCookingStartUnowned(
                target,
                $"料理已经开火，但厨具状态无法安全验证；自动化已暂停并保留该厨具供玩家处理：{startDiagnostic}");
        }

        if (!RuntimeCookingGenerationTracker.TryGetOwnershipSnapshot(
                cookController,
                out var validatedOwnership,
                out var validatedOwnershipDiagnostic)
            || validatedOwnership != ownershipSnapshot)
        {
            AppendAutomationLog(
                "start-unowned",
                collectionTarget,
                $"{recipeName}: ownership changed during native start callbacks; initial={ownershipDiagnostic}; "
                + $"current={validatedOwnershipDiagnostic}; cooker={DescribeCookController(cookController)}");
            return BlockCookingStartUnowned(
                target,
                $"料理开火回调期间厨具内容所有权已经变化，已交还玩家手动处理且不会自动操作该厨具："
                + validatedOwnershipDiagnostic);
        }

        AutomationCookingJob cookingJob;
        try
        {
            cookingJob = RegisterAutomationCookingJob(
                cookController,
                cookerReservation,
                controllerPointer,
                ownershipSnapshot,
                recipePointer,
                finalFood,
                recipeName,
                target,
                autoCollect,
                autoCompleteOrder,
                specialFoodTargetRevision);
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            AppendAutomationLog(
                "start-unowned",
                collectionTarget,
                $"{recipeName}: job registration failed: {message}; generation={ownershipSnapshot.Generation}; "
                + $"contentRevision={ownershipSnapshot.ContentRevision}; cooker={DescribeCookController(cookController)}");
            return BlockCookingStartUnowned(
                target,
                $"料理已经开火，但自动料理 job 登记失败；自动化已暂停并保留该厨具供玩家处理：{message}");
        }

        var extraText = extraIngredientIds.Count == 0 ? "不加料" : string.Join(",", extraIngredientIds);
        AppendAutomationLog(
            "start-ok",
            collectionTarget,
            $"{recipeName}; cooker={DescribeCookController(cookController)}; autoCollect={autoCollect}; "
            + $"extra={extraText}; {cookerValidationMessage}; {startDiagnostic}");
        return CookingStartResult.Succeeded(
            $"{recipeName} 已开始制作（配方 #{recipeId}，加料：{extraText}）。",
            qteResult.Message,
            qteResult.Skipped,
            jobId: cookingJob.JobId);
    }

    private static CookingStartResult BlockCookingStartUnowned(
        CookingCollectionTarget target,
        string message)
    {
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.CookingStartUnowned,
            target,
            message,
            outcome: "blocked",
            reasonCode: "cooking-start-unowned",
            terminal: true);
        return CookingStartResult.Failed(message, OrderPreparationStepCodes.CookingStartUnowned);
    }

    private static bool TryValidateCookerStart(
        object cookController,
        object recipe,
        int targetFoodId,
        out string diagnostic)
    {
        if (!TryReadExactMemberValue(
                cookController,
                out var rawPhase,
                out var phaseDiagnostic,
                "Phase",
                "<Phase>k__BackingField"))
        {
            diagnostic = $"厨具阶段不可读：{phaseDiagnostic}";
            return false;
        }

        var phase = ToInt(rawPhase, -1);
        if (phase < 0)
        {
            diagnostic = $"厨具阶段值无效：{rawPhase}";
            return false;
        }
        var result = ReadCookControllerResult(cookController, out var invalidResultDiagnostic);
        if (result == null)
        {
            diagnostic = string.IsNullOrWhiteSpace(invalidResultDiagnostic)
                ? $"未读取到厨具成品对象，phase={phase}"
                : $"厨具成品对象无效：{invalidResultDiagnostic}，phase={phase}";
            return false;
        }

        var resultFoodId = ReadSellableId(result);
        if (!IsSellable(result, sellableType: 0, id: targetFoodId))
        {
            diagnostic = $"厨具成品不是目标料理：actual=#{resultFoodId}; expected=#{targetFoodId}; phase={phase}";
            return false;
        }

        var chosenRecipe = ReadCookControllerChosenRecipe(cookController);
        if (chosenRecipe == null)
        {
            diagnostic = $"未读取到厨具目标配方，result=#{resultFoodId}; phase={phase}";
            return false;
        }

        var chosenFoodId = ToInt(ReadMember(chosenRecipe, "foodID"), int.MinValue);
        if (!IsSameObject(chosenRecipe, recipe) && chosenFoodId != targetFoodId)
        {
            diagnostic = $"厨具目标配方不匹配：chosenFood=#{chosenFoodId}; expectedFood=#{targetFoodId}; result=#{resultFoodId}; phase={phase}";
            return false;
        }

        if (phase == 0)
        {
            diagnostic = $"厨具仍为空闲状态：result=#{resultFoodId}; chosenFood=#{chosenFoodId}; phase={phase}";
            return false;
        }

        diagnostic = $"startValidated=1; phase={phase}; result=#{resultFoodId}; chosenFood=#{chosenFoodId}";
        return true;
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
    /// 同一目标只保留一个回执；同一厨具只保留一个仍持有厨具内容的 job。
    /// 已进入手动交接的回执不再占用厨具，可与该厨具后续的新 job 并存。
    /// </remarks>
    private static AutomationCookingJob RegisterAutomationCookingJob(
        object cookController,
        RuntimeCookerReservation cookerReservation,
        nint controllerPointer,
        RuntimeCookingOwnershipSnapshot ownershipSnapshot,
        nint chosenRecipePointer,
        object initialResult,
        string recipeName,
        CookingCollectionTarget target,
        bool autoDeliverFood,
        bool autoCompleteOrder,
        long specialFoodTargetRevision)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive)
        {
            throw new InvalidOperationException("Night-business session ended before cooking job registration.");
        }

        var sessionGeneration = lifecycle.Generation;
        lock (AutomationCookingJobLock)
        {
            lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            if (!lifecycle.IsActive || lifecycle.Generation != sessionGeneration)
            {
                throw new InvalidOperationException("Night-business session changed before cooking job registration.");
            }

            if (!RuntimeCookingGenerationTracker.TryGetOwnershipSnapshot(
                    cookController,
                    out var registrationOwnership,
                    out var registrationOwnershipDiagnostic)
                || registrationOwnership != ownershipSnapshot)
            {
                throw new InvalidOperationException(
                    "Cooking ownership changed before job registration: "
                    + registrationOwnershipDiagnostic);
            }

            var replacedJobs = AutomationCookingJobs
                .Where(job => !job.ManualHandoffObserved
                    && (IsSameCookingCollectionTarget(job.Target, target)
                        || job.ControllerPointer == controllerPointer))
                .ToArray();
            foreach (var replacedJob in replacedJobs)
            {
                RecordAutomationRuntimeEvent(
                    OrderPreparationStepCodes.CookingCancelled,
                    replacedJob,
                    $"{replacedJob.RecipeName} 自动料理任务被同目标的新任务替换；厨具状态保持原样。",
                    outcome: "cancelled",
                    reasonCode: "cooking-job-replaced",
                    terminal: true);
                AutomationCookingJobs.Remove(replacedJob);
            }

            var nowUtc = DateTime.UtcNow;
            var phase = ToInt(TryInvokeInstanceValue(cookController, "get_Phase") ?? ReadMember(cookController, "Phase"), -1);
            var progress = ToFloat(TryInvokeInstanceValue(cookController, "get_CookingProgress") ?? ReadMember(cookController, "CookingProgress"), 0f);
            TryReadNativeObjectPointer(initialResult, out var resultPointer);
            lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            if (!lifecycle.IsActive || lifecycle.Generation != sessionGeneration)
            {
                throw new InvalidOperationException("Night-business session changed during cooking job registration.");
            }

            AutomationCookingJobSequence++;
            var job = new AutomationCookingJob
            {
                JobId = $"CJ-{AutomationCookingJobSequence:D6}",
                CookerReservation = cookerReservation,
                ControllerPointer = controllerPointer,
                Generation = ownershipSnapshot.Generation,
                ContentRevision = ownershipSnapshot.ContentRevision,
                ChosenRecipePointer = chosenRecipePointer,
                RecipeName = recipeName,
                CreatedAtUtc = nowUtc,
                Target = target,
                AutoDeliverFood = autoDeliverFood,
                AutoCompleteOrder = autoCompleteOrder,
                SpecialFoodTargetRevision = specialFoodTargetRevision,
                Tracker = new AutomationCookingJobTracker(ownershipSnapshot.Generation, nowUtc, phase, progress),
                DeliveryTimeoutClock = new AutomationEffectiveTimeoutClock(nowUtc, initiallyEligible: false),
                ManualHandoffMissingOrderClock = new AutomationEffectiveTimeoutClock(nowUtc, initiallyEligible: false),
                ManualHandoffReadFailureClock = new AutomationEffectiveTimeoutClock(nowUtc, initiallyEligible: false),
                CurrentResultPointer = resultPointer,
            };
            AutomationCookingJobs.Add(job);
            AppendAutomationLog(
                "job-add",
                target,
                job.FormatLogContext($"recipe={recipeName}; replaced={replacedJobs.Length}"));
            if (target.SpecialFoodTargetPolicy != null)
            {
                AppendSpecialFoodTargetCookingJobDiagnostic(
                    "job-add",
                    job,
                    "registered",
                    detail: job.FormatLogContext($"recipe={recipeName}; replaced={replacedJobs.Length}"));
            }

            return job;
        }
    }

    /// <summary>
    /// 判断指定普客订单是否已有目标料理正在制作。
    /// </summary>
    /// <remarks>
    /// 优先用前端锁定的订单 key 匹配；key 缺失时只接受同一原生订单对象。
    /// </remarks>
    private static bool HasNormalOrderCookingJob(string orderKey, object order, int deskCode, int foodId, int beverageId, out string message)
    {
        lock (AutomationCookingJobLock)
        {
            foreach (var job in AutomationCookingJobs)
            {
                if (!IsMatchingNormalOrderCookingJob(job, orderKey, order, deskCode, foodId, beverageId)) continue;
                message = FormatNormalOrderCookingJobMessage(job, deskCode);
                return true;
            }

        }

        message = "";
        return false;
    }

    private static (bool Delivered, bool CompletedOrder, string StepName, string Message, string Code)
        TryProcessNormalOrderCookingJob(
            string orderKey,
            object order,
            int deskCode,
            int foodId,
            int beverageId)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive)
        {
            return (false, false, "普客送达料理", "夜间经营会话已结束，已停止料理任务处理。", OrderPreparationStepCodes.NightBusinessLifecycleUnavailable);
        }

        var sessionGeneration = lifecycle.Generation;
        lock (AutomationCookingJobLock)
        {
            for (var i = AutomationCookingJobs.Count - 1; i >= 0; i--)
            {
                var job = AutomationCookingJobs[i];
                if (!IsMatchingNormalOrderCookingJob(job, orderKey, order, deskCode, foodId, beverageId)) continue;

                (bool Remove, string Message, string Code) result;
                try
                {
                    result = TryProcessAutomationCookingJob(job);
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
                lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
                if (!lifecycle.IsActive
                    || lifecycle.Generation != sessionGeneration
                    || i >= AutomationCookingJobs.Count
                    || !ReferenceEquals(AutomationCookingJobs[i], job))
                {
                    return (false, false, "普客送达料理", "夜间经营会话已结束，已停止料理任务处理。", OrderPreparationStepCodes.NightBusinessLifecycleUnavailable);
                }

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    AppendAutomationLog("job", job.Target, job.FormatLogContext(result.Message));
                }

                if (result.Remove)
                {
                    AppendAutomationLog("job-remove", job.Target, job.FormatLogContext($"age={(DateTime.UtcNow - job.CreatedAtUtc).TotalSeconds:F1}s; code={result.Code}"));
                    AutomationCookingJobs.RemoveAt(i);
                }

                if (job.FoodDeliveryEvaluationState == AutomationFoodDeliveryEvaluationState.Completed)
                {
                    return (true, true, "普客送达料理", string.IsNullOrWhiteSpace(result.Message)
                        ? $"{job.Target.FoodName} 已直接送达普客订单并完成评价。"
                        : result.Message,
                        result.Code);
                }

                if (job.FoodDeliveryEvaluationState == AutomationFoodDeliveryEvaluationState.CommitUncertain)
                {
                    return (false, false, "普客送达料理", result.Message, result.Code);
                }

                if (result.Code == OrderPreparationStepCodes.FoodDelivered)
                {
                    return (true, false, "普客送达料理", string.IsNullOrWhiteSpace(result.Message)
                        ? $"{job.Target.FoodName} 已直接送达普客订单。"
                        : result.Message,
                        string.IsNullOrWhiteSpace(result.Code) ? OrderPreparationStepCodes.FoodDelivered : result.Code);
                }

                if (result.Remove && IsYuumaBossTarget(job.Target))
                {
                    return (false, false, "普客送达料理", result.Message, result.Code);
                }

                if (ReadOrderServedFood(order) != null)
                {
                    return (true, false, "普客送达料理", string.IsNullOrWhiteSpace(result.Message)
                        ? $"{job.Target.FoodName} 已直接送达普客订单。"
                        : result.Message,
                        string.IsNullOrWhiteSpace(result.Code) ? OrderPreparationStepCodes.FoodDelivered : result.Code);
                }

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    return (false, false, "普客送达料理", result.Message, result.Code);
                }

                return (false, false, "普客开始料理", FormatNormalOrderCookingJobMessage(job, deskCode), OrderPreparationStepCodes.CookingPending);
            }
        }

        return (false, false, "", "", "");
    }

    private static bool IsMatchingNormalOrderCookingJob(AutomationCookingJob job, string orderKey, object order, int deskCode, int foodId, int beverageId)
    {
        if (job.Target.Kind != CookingCollectionTargetKind.NormalOrder) return false;
        if (job.Target.FoodId != foodId) return false;
        if (job.Target.BeverageId >= 0 && beverageId >= 0 && job.Target.BeverageId != beverageId) return false;
        if (!string.IsNullOrWhiteSpace(orderKey) && !string.IsNullOrWhiteSpace(job.Target.OrderKey))
        {
            return string.Equals(orderKey, job.Target.OrderKey, StringComparison.Ordinal);
        }

        return job.Target.Order != null && IsSameObject(job.Target.Order, order);
    }

    private static string FormatNormalOrderCookingJobMessage(AutomationCookingJob job, int deskCode)
    {
        var targetText = job.Target.DeskCode == deskCode
            ? $"桌 {deskCode + 1} 的目标料理 {job.Target.FoodName}"
            : $"目标料理 {job.Target.FoodName}";
        return WillAutomaticallyDeliverCookingJob(job)
            ? $"{targetText} 已在制作中，等待完成后会自动直接送达。"
            : $"{targetText} 已登记手动交接回执；Mod 不会送达、入箱或复位厨具。";
    }

    private static bool WillAutomaticallyDeliverCookingJob(AutomationCookingJob job)
    {
        return WillAutomaticallyDeliverCookingTarget(
            job.AutoDeliverFood,
            job.AutoCompleteOrder);
    }

    private static bool WillAutomaticallyDeliverCookingTarget(
        bool autoDeliverFood,
        bool autoCompleteOrder)
    {
        return autoDeliverFood && autoCompleteOrder;
    }

    private static bool TryFindAutomationCookingJob(
        CookingCollectionTarget target,
        bool requestAutoDelivery,
        bool requestAutoCompletion,
        out AutomationCookingJob? existingJob,
        out string message)
    {
        lock (AutomationCookingJobLock)
        {
            foreach (var job in AutomationCookingJobs)
            {
                if (!IsSameCookingCollectionTarget(job.Target, target)) continue;

                if (requestAutoDelivery && !job.AutoDeliverFood && !job.ManualHandoffObserved)
                {
                    job.AutoDeliverFood = true;
                }

                if (requestAutoCompletion && !job.AutoCompleteOrder && !job.ManualHandoffObserved)
                {
                    job.AutoCompleteOrder = true;
                }

                existingJob = job;
                message = job.ManualHandoffExpired
                    ? $"同一订单仍有过期目标料理 {job.Target.FoodName} 等待玩家处理；"
                        + "当前目标不会重复开锅，其他订单不受影响。"
                    : WillAutomaticallyDeliverCookingJob(job)
                    ? $"目标料理 {job.Target.FoodName} 已在制作中，等待完成后会自动直接送达。"
                    : $"目标料理 {job.Target.FoodName} 已进入手动交接，Mod 只保留防重复开锅回执，不会送达、入箱或复位厨具。";
                return true;
            }

            if (IsYuumaBossTarget(target))
            {
                foreach (var job in AutomationCookingJobs)
                {
                    if (!job.ManualHandoffObserved
                        || !IsYuumaBossTarget(job.Target)
                        || !IsSameCookingOrderIdentity(job.Target, target))
                    {
                        continue;
                    }

                    existingJob = job;
                    message = job.ManualHandoffExpired
                        ? $"同一订单仍有过期目标料理 {job.Target.FoodName} 等待玩家处理；"
                            + "当前目标不会重复开锅，其他订单不受影响。"
                        : $"同一订单的目标料理 {job.Target.FoodName} 已进入手动交接；"
                            + "交接完成前不会为该订单的目标变化重复开锅。";
                    return true;
                }
            }
        }

        existingJob = null;
        message = "";
        return false;
    }

    private static bool IsSameCookingOrderIdentity(
        CookingCollectionTarget left,
        CookingCollectionTarget right)
    {
        if (left.Kind != right.Kind
            || !string.Equals(
                left.SpecialBusinessRole,
                right.SpecialBusinessRole,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (left.Kind == CookingCollectionTargetKind.RareOrder)
        {
            var leftHasTrace = !string.IsNullOrWhiteSpace(left.TraceId);
            var rightHasTrace = !string.IsNullOrWhiteSpace(right.TraceId);
            if (leftHasTrace != rightHasTrace
                || leftHasTrace
                && !string.Equals(left.TraceId, right.TraceId, StringComparison.Ordinal))
            {
                return false;
            }

            return RareOrderIdentityMatcher.Matches(
                new RareOrderIdentity(
                    left.DeskCode >= 0 ? left.DeskCode : null,
                    left.RuntimeGuestId,
                    left.FoodTagId,
                    left.BeverageTagId),
                new RareOrderIdentity(
                    right.DeskCode >= 0 ? right.DeskCode : null,
                    right.RuntimeGuestId,
                    right.FoodTagId,
                    right.BeverageTagId),
                out _);
        }

        if (!string.IsNullOrWhiteSpace(left.OrderKey)
            && !string.IsNullOrWhiteSpace(right.OrderKey))
        {
            return string.Equals(left.OrderKey, right.OrderKey, StringComparison.Ordinal);
        }

        return left.Order != null
            && right.Order != null
            && IsSameObject(left.Order, right.Order);
    }

    /// <summary>
    /// 判断两个出锅处理目标是否代表同一个业务目标。
    /// </summary>
    private static bool IsSameCookingCollectionTarget(CookingCollectionTarget left, CookingCollectionTarget right)
    {
        if (left.Kind != right.Kind) return false;
        if (!string.Equals(
                left.SpecialBusinessRole,
                right.SpecialBusinessRole,
                StringComparison.Ordinal))
        {
            return false;
        }

        var leftSpecialPolicy = left.SpecialFoodTargetPolicy;
        var rightSpecialPolicy = right.SpecialFoodTargetPolicy;
        if ((leftSpecialPolicy == null) != (rightSpecialPolicy == null))
        {
            return false;
        }

        if (leftSpecialPolicy != null
            && rightSpecialPolicy != null
            && !leftSpecialPolicy.HasSameIdentity(rightSpecialPolicy))
        {
            return false;
        }

        if (left.SpecialFoodTargetRevision != right.SpecialFoodTargetRevision)
        {
            return false;
        }

        if (left.AllowYuumaControlledProgression != right.AllowYuumaControlledProgression)
        {
            return false;
        }

        if (left.Kind == CookingCollectionTargetKind.RareOrder)
        {
            return RareOrderIdentityMatcher.IsSameCookingTarget(
                left.TraceId,
                left.FoodId,
                new RareOrderIdentity(left.DeskCode >= 0 ? left.DeskCode : null, left.RuntimeGuestId, left.FoodTagId, left.BeverageTagId),
                right.TraceId,
                right.FoodId,
                new RareOrderIdentity(right.DeskCode >= 0 ? right.DeskCode : null, right.RuntimeGuestId, right.FoodTagId, right.BeverageTagId));
        }

        if (left.Kind != CookingCollectionTargetKind.NormalOrder) return false;
        if (left.FoodId != right.FoodId) return false;
        if (!string.IsNullOrWhiteSpace(left.OrderKey) && !string.IsNullOrWhiteSpace(right.OrderKey))
        {
            return string.Equals(left.OrderKey, right.OrderKey, StringComparison.Ordinal);
        }

        return left.Order != null && right.Order != null && IsSameObject(left.Order, right.Order);
    }

    /// <summary>
    /// 从当前物理厨具目录重新绑定一个自动料理 job。
    /// </summary>
    /// <remarks>
    /// 返回的 controller 只允许在当前同步调用栈使用；job 不保存 IL2CPP wrapper。
    /// </remarks>
    private static bool TryReacquireAutomationCooker(
        AutomationCookingJob job,
        out RuntimeAutomationCookerBinding binding,
        out AutomationCookerReacquireFailureKind failureKind,
        out string diagnostic,
        RuntimeCookingContentMutation? expectedCompletedMutation = null,
        long minimumContentRevision = 0)
    {
        binding = null!;
        failureKind = AutomationCookerReacquireFailureKind.TransientUnavailable;

        YuumaCookerTopologyLease? yuumaTopologyLease = null;
        if (IsYuumaBossTarget(job.Target)
            && !YuumaCookerTopologyObserver.TryAcquireFreshLease(
                out yuumaTopologyLease,
                out var topologyLeaseDiagnostic))
        {
            diagnostic = $"yuuma-topology-lease-unavailable; {topologyLeaseDiagnostic}";
            return false;
        }

        var cookSystem = RuntimeCookerReflection.GetCookSystemManager();
        if (cookSystem == null)
        {
            diagnostic = "cook-system-manager-unavailable";
            return false;
        }

        if (!RuntimeCookerReflection.TryReadLockedCookerPositions(
                out var lockedPositions,
                out var lockedStatus))
        {
            diagnostic = $"locked-cookers-unavailable; {lockedStatus}";
            return false;
        }

        if (!RuntimeCookerReflection.TryReadCookerControllerEntriesFromCookSystem(
                cookSystem,
                lockedPositions,
                out var entries,
                out var entriesStatus))
        {
            diagnostic = $"all-cookers-unavailable; {entriesStatus}; {lockedStatus}";
            return false;
        }

        if (!job.CookerReservation.TryMatch(entries, out var entry, out var reservationError))
        {
            failureKind = AutomationCookerReacquireFailureKind.Invalidated;
            diagnostic = $"reservation-invalidated; {reservationError}; {entriesStatus}; {lockedStatus}";
            return false;
        }

        if (lockedPositions.Contains(job.CookerReservation.GridPosition))
        {
            failureKind = AutomationCookerReacquireFailureKind.Invalidated;
            diagnostic = $"controller-challenge-locked; reservation={job.CookerReservation}; "
                + $"{entriesStatus}; {lockedStatus}";
            return false;
        }

        if (!TryReadNativeObjectPointer(entry.Controller, out var controllerPointer))
        {
            diagnostic = $"controller-pointer-unavailable; reservation={job.CookerReservation}; {entriesStatus}";
            return false;
        }

        if (controllerPointer != job.ControllerPointer)
        {
            failureKind = AutomationCookerReacquireFailureKind.Invalidated;
            diagnostic = $"controller-pointer-changed; expected=0x{(long)job.ControllerPointer:X}; "
                + $"actual=0x{(long)controllerPointer:X}; reservation={job.CookerReservation}";
            return false;
        }

        if (!RuntimeCookingGenerationTracker.TryGetOwnershipSnapshot(
                entry.Controller,
                out var ownershipBefore,
                out var ownershipBeforeDiagnostic))
        {
            diagnostic = $"ownership-before-unavailable; {ownershipBeforeDiagnostic}";
            return false;
        }

        if (!RuntimeCookerReflection.TryReadCookerControllerState(
                entry.Controller,
                out var state,
                out var stateStatus))
        {
            diagnostic = $"controller-state-unavailable; {stateStatus}; {entriesStatus}; {lockedStatus}";
            return false;
        }

        var challengeGate = job.CookerReservation.EvaluateChallengeGate(
            lockedPositions,
            state.CouldOpen);
        if (challengeGate == RuntimeCookerChallengeGateState.Inconsistent)
        {
            diagnostic = $"challenge-gate-inconsistent; reservation={job.CookerReservation}; "
                + $"couldOpen={state.CouldOpen}; {stateStatus}; {lockedStatus}";
            return false;
        }

        if (state.IsEmptyDesk)
        {
            failureKind = AutomationCookerReacquireFailureKind.Invalidated;
            diagnostic = $"controller-became-empty-slot; reservation={job.CookerReservation}; {stateStatus}";
            return false;
        }

        object? rawProgress;
        try
        {
            rawProgress = TryInvokeInstanceValue(entry.Controller, "get_CookingProgress")
                ?? ReadMember(entry.Controller, "CookingProgress");
        }
        catch (Exception ex)
        {
            diagnostic = $"cooking-progress-unavailable; {ex.GetBaseException().Message}";
            return false;
        }

        if (rawProgress == null)
        {
            diagnostic = "cooking-progress-missing";
            return false;
        }

        var progress = ToFloat(rawProgress, float.NaN);
        if (float.IsNaN(progress) || float.IsInfinity(progress))
        {
            diagnostic = $"cooking-progress-invalid; value={rawProgress}";
            return false;
        }

        if (!RuntimeCookingGenerationTracker.TryGetOwnershipSnapshot(
                entry.Controller,
                out var ownershipAfter,
                out var ownershipAfterDiagnostic))
        {
            diagnostic = $"ownership-after-unavailable; {ownershipAfterDiagnostic}";
            return false;
        }

        if (ownershipBefore != ownershipAfter)
        {
            failureKind = AutomationCookerReacquireFailureKind.Invalidated;
            diagnostic = $"ownership-changed-during-rebind; before={ownershipBeforeDiagnostic}; "
                + $"after={ownershipAfterDiagnostic}";
            return false;
        }

        var ownershipMatches = expectedCompletedMutation.HasValue
            ? ownershipAfter.Generation == job.Generation
                && ownershipAfter.ContentRevision > minimumContentRevision
                && ownershipAfter.LastMutation == expectedCompletedMutation.Value
                && ownershipAfter.MutationCompleted
            : ownershipAfter.Generation == job.Generation
                && ownershipAfter.ContentRevision == job.ContentRevision;
        if (!ownershipMatches)
        {
            failureKind = AutomationCookerReacquireFailureKind.Invalidated;
            var expected = expectedCompletedMutation.HasValue
                ? $"generation={job.Generation}; mutation={expectedCompletedMutation}; "
                    + $"completed=true; contentRevision>{minimumContentRevision}"
                : $"generation={job.Generation}; contentRevision={job.ContentRevision}";
            diagnostic = $"ownership-invalidated; expected={expected}; "
                + $"actual={ownershipAfter.Generation}/{ownershipAfter.ContentRevision}; "
                + $"mutation={ownershipAfter.LastMutation}; completed={ownershipAfter.MutationCompleted}; "
                + $"{ownershipAfterDiagnostic}";
            return false;
        }

        if (yuumaTopologyLease != null
            && !YuumaCookerTopologyObserver.TryValidateFreshLease(
                yuumaTopologyLease,
                out var topologyValidationDiagnostic))
        {
            failureKind = AutomationCookerReacquireFailureKind.TransientUnavailable;
            diagnostic = $"yuuma-topology-lease-invalidated; {topologyValidationDiagnostic}";
            return false;
        }

        binding = new RuntimeAutomationCookerBinding(
            entry.Controller,
            state,
            ownershipAfter,
            progress);
        failureKind = AutomationCookerReacquireFailureKind.None;
        diagnostic = $"fresh-binding-ok; reservation={job.CookerReservation}; "
            + $"ownership={ownershipAfter.Generation}/{ownershipAfter.ContentRevision}; "
            + $"{stateStatus}; {entriesStatus}; {lockedStatus}";
        return true;
    }

    private static (bool Remove, string Message, string Code) HandleAutomationCookerReacquireFailure(
        AutomationCookingJob job,
        DateTime observedAtUtc,
        AutomationCookerReacquireFailureKind failureKind,
        string diagnostic)
    {
        var failureCode = failureKind == AutomationCookerReacquireFailureKind.Invalidated
            ? "cooking-cooker-invalidated"
            : "cooking-cooker-rebind-unavailable";
        var changed = !string.Equals(job.CookerBindingFailureCode, failureCode, StringComparison.Ordinal)
            || !string.Equals(job.CookerBindingDiagnostic, diagnostic, StringComparison.Ordinal);
        job.CookerBindingFailureCode = failureCode;
        job.CookerBindingDiagnostic = diagnostic;
        job.DeliveryTimeoutClock.Observe(observedAtUtc, eligible: false);
        job.Tracker.Suspend(observedAtUtc);

        if (failureKind != AutomationCookerReacquireFailureKind.Invalidated)
        {
            return (
                false,
                changed
                    ? $"{job.RecipeName} 的物理厨具快照暂不可完整复核，本轮不会读取成品、送达、入箱或复位厨具：{diagnostic}"
                    : "",
                OrderPreparationStepCodes.CookingPending);
        }

        var message = $"{job.RecipeName} 的原厨具已被挑战锁定、移除、替换或进入其他锅次；"
            + "旧自动料理任务已退出且不会访问旧厨具对象，将由当前物理厨具快照重新规划。"
            + diagnostic;
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.CookingOwnershipLost,
            job,
            message,
            outcome: "interrupted",
            reasonCode: failureCode,
            terminal: true);
        return (true, message, OrderPreparationStepCodes.CookingOwnershipLost);
    }

    /// <summary>
    /// 读取一个自动料理 job 的本锅成品并按场景策略处理。
    /// </summary>
    /// <returns>
    /// <c>Remove</c> 表示该 job 是否应从集合删除；<c>Message</c> 是需要展示或记录的处理结果。
    /// </returns>
    /// <remarks>
    /// 游戏完成料理后，CookController 的阶段和 Result 字段并不总是在同一帧稳定，因此这里同时看阶段、成品对象和等待时间。
    /// </remarks>
    private static (bool Remove, string Message, string Code) TryProcessAutomationCookingJob(
        AutomationCookingJob job,
        bool timeoutEligible = true)
    {
        var nowUtc = DateTime.UtcNow;
        if (!timeoutEligible)
        {
            job.DeliveryTimeoutClock.Observe(nowUtc, eligible: false);
            job.ManualHandoffMissingOrderClock.Observe(nowUtc, eligible: false);
            job.ManualHandoffReadFailureClock.Observe(nowUtc, eligible: false);
            job.Tracker.Suspend(nowUtc);
            return (false, "", OrderPreparationStepCodes.CookingPending);
        }

        if (job.ManualHandoffObserved)
        {
            if (TryGetUnresolvedAutomationSafetyBarrier(job.Target, out var manualHandoffBarrier))
            {
                job.ManualHandoffMissingOrderClock.Observe(nowUtc, eligible: false);
                job.ManualHandoffReadFailureClock.Observe(nowUtc, eligible: false);
                job.Tracker.Suspend(nowUtc);
                return (
                    false,
                    $"同一订单存在未确认安全事件 #{manualHandoffBarrier!.Sequence}，手动交接回执已暂停。",
                    OrderPreparationStepCodes.CookingPending);
            }

            return TryProcessManualHandoffReceipt(job, nowUtc);
        }

        if (job.FoodDeliveryCommitted
            && (job.FoodDeliveryCleanupCompleted || job.FoodDeliveryCleanupTerminal))
        {
            return TryCompleteCommittedFoodDeliveryTransaction(job);
        }

        if (!TryReacquireAutomationCooker(
                job,
                out var cookerBinding,
                out var reacquireFailure,
                out var reacquireDiagnostic))
        {
            return HandleAutomationCookerReacquireFailure(
                job,
                nowUtc,
                reacquireFailure,
                reacquireDiagnostic);
        }

        job.CookerBindingFailureCode = "";
        job.CookerBindingDiagnostic = reacquireDiagnostic;
        var contentState = new RuntimeCookerContentState
        {
            Phase = cookerBinding.State.Phase,
            Result = cookerBinding.State.Result,
            ChosenRecipe = cookerBinding.State.ChosenRecipe,
        };
        var contentDiagnostic = reacquireDiagnostic;
        var phase = contentState.Phase;
        var progress = cookerBinding.Progress;
        var ownershipAfter = cookerBinding.Ownership;
        var invalidResultDiagnostic = "";

        job.DeliveryTimeoutClock.Observe(
            nowUtc,
            timeoutEligible && phase == 3);
        if (job.WarmerStoreCommitUncertain)
        {
            return BlockUncertainWarmerStore(job, "保温箱提交状态无法确认，禁止继续处理厨具。");
        }

        if (job.FoodDeliveryCommitUncertain)
        {
            return BlockUncertainFoodDelivery(job, "订单送达提交状态无法确认，禁止继续处理厨具。");
        }

        if (job.WarmerStoreCommitted)
        {
            return timeoutEligible
                ? TryCompleteCommittedWarmerReset(job)
                : (false, "", OrderPreparationStepCodes.CookingPending);
        }

        if (job.FoodDeliveryCommitted)
        {
            return timeoutEligible
                ? TryCompleteCommittedFoodDeliveryTransaction(job)
                : (false, "", OrderPreparationStepCodes.CookingPending);
        }

        if (TryGetUnresolvedAutomationSafetyBarrier(job.Target, out var activeBarrier))
        {
            job.DeliveryTimeoutClock.Observe(nowUtc, eligible: false);
            job.Tracker.Suspend(nowUtc);
            return (
                false,
                $"同一订单存在未确认安全事件 #{activeBarrier!.Sequence}，料理 job 已暂停，不会送达、入箱或复位厨具。",
                OrderPreparationStepCodes.CookingPending);
        }

        object? cookedFood = null;
        var observedGeneration = ownershipAfter.Generation;
        AutomationCookingObservationKind observationKind;
        if (contentState.IsExactReset)
        {
            observationKind = AutomationCookingObservationKind.OwnershipLost;
            invalidResultDiagnostic = $"{contentDiagnostic}; exact native cooker reset observed";
        }
        else if (contentState.ChosenRecipe == null)
        {
            observationKind = AutomationCookingObservationKind.Unreadable;
            invalidResultDiagnostic = $"{contentDiagnostic}; non-idle cooker has no chosen recipe";
        }
        else if (!TryReadNativeObjectPointer(contentState.ChosenRecipe, out var chosenRecipePointer))
        {
            observationKind = AutomationCookingObservationKind.Unreadable;
            invalidResultDiagnostic = $"{contentDiagnostic}; chosen recipe pointer unavailable";
        }
        else if (chosenRecipePointer != job.ChosenRecipePointer)
        {
            observationKind = AutomationCookingObservationKind.OwnershipLost;
            invalidResultDiagnostic = $"{contentDiagnostic}; expectedRecipe=0x{(long)job.ChosenRecipePointer:X}; "
                + $"actualRecipe=0x{(long)chosenRecipePointer:X}";
        }
        else if (contentState.Result == null)
        {
            observationKind = AutomationCookingObservationKind.Missing;
            invalidResultDiagnostic = contentDiagnostic;
        }
        else if (TryAcceptCookControllerFoodResult(
                     contentState.Result,
                     "CookController.get_Result",
                     out cookedFood,
                     out invalidResultDiagnostic))
        {
            observationKind = AutomationCookingObservationKind.Owned;
        }
        else
        {
            observationKind = AutomationCookingObservationKind.Unreadable;
        }

        if (cookedFood != null && TryReadNativeObjectPointer(cookedFood, out var resultPointer))
        {
            job.CurrentResultPointer = resultPointer;
        }

        var transition = job.Tracker.Observe(new AutomationCookingObservation(
            nowUtc,
            observedGeneration,
            observationKind,
            phase,
            progress,
            invalidResultDiagnostic,
            timeoutEligible));

        if (transition.Terminal)
        {
            if (!IsYuumaBossTarget(job.Target)
                && !job.AutoDeliverFood
                && transition.ReasonCode is "cooking-controller-reused" or "cooking-ownership-lost")
            {
                return EnterManualHandoff(job, nowUtc);
            }

            var (code, message) = transition.ReasonCode switch
            {
                "cooking-controller-reused" => (
                    OrderPreparationStepCodes.CookingControllerReused,
                    $"{job.RecipeName} 自动料理任务检测到同一厨具已开始新一锅，旧任务已退出且不会操作新成品。"),
                "cooking-ownership-lost" => (
                    OrderPreparationStepCodes.CookingOwnershipLost,
                    $"{job.RecipeName} 的厨具成品已离开 Mod 所有的厨具内容或被外部替换；"
                    + "旧任务已释放，并将在订单仍未送达时重新准备。"),
                "cooking-result-missing" => (
                    OrderPreparationStepCodes.CookingResultUnreadable,
                    $"{job.RecipeName} 的厨具成品在非空闲阶段持续缺失，无法确认当前锅次状态；"
                    + "自动料理任务已停止并保留厨具当前状态，请人工确认。"),
                "cooking-progress-stalled" => (
                    OrderPreparationStepCodes.CookingProgressStalled,
                    $"{job.RecipeName} 的制作进度长时间未变化，无法确认锅次是否仍可安全推进；自动料理任务已停止并保留厨具当前状态，请人工确认。"),
                "cooking-progress-regressed" => (
                    OrderPreparationStepCodes.CookingProgressRegressed,
                    $"{job.RecipeName} 的厨具制作进度连续回退，无法确认当前锅次状态；自动料理任务已停止且保留厨具当前状态。"),
                _ => (
                    OrderPreparationStepCodes.CookingResultUnreadable,
                    $"{job.RecipeName} 的厨具成品连续无法安全读取，自动料理任务已停止且保留厨具当前状态：{invalidResultDiagnostic}"),
            };
            RecordAutomationRuntimeEvent(
                code,
                job,
                message,
                outcome: transition.Outcome,
                reasonCode: transition.ReasonCode,
                terminal: true);
            return (true, message, code);
        }

        if (transition.Directive == AutomationCookingJobDirective.DeliverOwnedResult && cookedFood != null)
        {
            if (IsYuumaBossTarget(job.Target))
            {
                return TryDeliverAutomationCookedFood(job, cookedFood);
            }

            if (!job.AutoDeliverFood)
            {
                return EnterManualHandoff(job, nowUtc);
            }

            return TryDeliverAutomationCookedFood(job, cookedFood);
        }

        return (false, "", OrderPreparationStepCodes.CookingPending);
    }

    private static (bool Remove, string Message, string Code) EnterManualHandoff(
        AutomationCookingJob job,
        DateTime observedAtUtc)
    {
        job.ManualHandoffObserved = true;
        job.Tracker.EnterManualHandoff(observedAtUtc);
        return (
            false,
            $"{job.RecipeName} 已进入手动交接；Mod 只保留同订单防重复开锅回执，不会送达、入箱或复位当前厨具。",
            OrderPreparationStepCodes.CookingPending);
    }

    private static (bool Remove, string Message, string Code) TryProcessManualHandoffReceipt(
        AutomationCookingJob job,
        DateTime observedAtUtc)
    {
        try
        {
            var targetChangedMessage = "";
            if (IsYuumaBossTarget(job.Target)
                && !job.ManualHandoffExpired)
            {
                var targetChanged = TryDetectSpecialFoodTargetPolicyChanged(
                    job,
                    out var originalSignature,
                    out var currentSignature,
                    out var originalTags,
                    out var currentTags,
                    out var originalRevision,
                    out var currentRevision,
                    out var targetComparisonAvailable);
                if (targetComparisonAvailable && targetChanged)
                {
                    job.ManualHandoffExpired = true;
                    job.Tracker.MarkManualHandoffExpired(observedAtUtc);
                    targetChangedMessage =
                        $"桌 {job.Target.DeskCode + 1} 的 {job.RecipeName} 已成为过期交接成品："
                        + $"开锅目标 revision={originalRevision} "
                        + $"{FormatSpecialFoodTargetForMessage(originalSignature, originalTags)}，"
                        + $"当前目标 revision={currentRevision} "
                        + $"{FormatSpecialFoodTargetForMessage(currentSignature, currentTags)}。"
                        + "同一订单在该成品处理完前不会重复开锅；请勿将它作为当前目标料理送达。"
                        + "Mod 未操作托盘、厨具或成品。";
                    RecordAutomationRuntimeEvent(
                        OrderPreparationStepCodes.CookingManualHandoffExpired,
                        job,
                        targetChangedMessage,
                        outcome: "waiting",
                        reasonCode: "cooking-manual-handoff-expired",
                        terminal: false);
                }
            }

            var request = BuildOrderRequestFromCookingJob(job);
            var runtimeOrder = IsYuumaBossTarget(job.Target)
                ? FindYuumaRuntimeOrder(job.Target, request)
                : job.Target.Kind == CookingCollectionTargetKind.NormalOrder
                    ? FindRuntimeNormalOrder(request)
                    : FindRuntimeOrder(request, RuntimeOrderLookupPurpose.Completion);
            if (runtimeOrder.Order == null)
            {
                job.ManualHandoffReadFailureCount = 0;
                job.ManualHandoffReadFailureClock.Reset(observedAtUtc, eligible: false);
                job.ManualHandoffMissingOrderCount++;
                job.ManualHandoffMissingOrderClock.Observe(observedAtUtc, eligible: true);
                if (job.ManualHandoffMissingOrderCount < MissingTargetRetireAttempts
                    || job.ManualHandoffMissingOrderClock.Elapsed < MissingTargetRetireDelay)
                {
                    return (false, targetChangedMessage, OrderPreparationStepCodes.CookingPending);
                }

                var missingMessage = $"{job.RecipeName} 的手动交接目标订单已连续不可见，防重复开锅回执已释放；Mod 未操作厨具或成品。";
                RecordAutomationRuntimeEvent(
                    OrderPreparationStepCodes.CookingManualHandoffCompleted,
                    job,
                    missingMessage,
                    outcome: "completed",
                    reasonCode: "cooking-manual-handoff-order-finished",
                    terminal: true);
                return (true, missingMessage, OrderPreparationStepCodes.CookingManualHandoffCompleted);
            }

            job.ManualHandoffMissingOrderCount = 0;
            job.ManualHandoffMissingOrderClock.Reset(observedAtUtc, eligible: true);
            if (!TryReadOrderServedItem(
                    runtimeOrder.Order,
                    RuntimeDeliveryItemKind.Food,
                    out var servedFood,
                    out var servedFoodDiagnostic))
            {
                return HandleManualHandoffReadFailure(job, observedAtUtc, servedFoodDiagnostic);
            }

            job.ManualHandoffReadFailureCount = 0;
            job.ManualHandoffReadFailureClock.Reset(observedAtUtc, eligible: false);
            if (servedFood == null)
            {
                return (false, targetChangedMessage, OrderPreparationStepCodes.CookingPending);
            }

            var servedPointerReadable = TryReadNativeObjectPointer(servedFood, out var servedPointer);
            var exactJobResult = servedPointerReadable
                && job.CurrentResultPointer != 0
                && servedPointer == job.CurrentResultPointer;
            var actualIdentityReadable = TryReadCookControllerFoodResultIdentity(
                servedFood,
                "OrderBase.ServFood",
                out var actualIdentity,
                out _);
            var resolution = exactJobResult
                ? "本 job 的精确成品"
                : servedPointerReadable && actualIdentityReadable
                    ? actualIdentity.FoodId == job.Target.FoodId
                        ? "同料理的其他原生对象"
                        : $"其他料理（id={actualIdentity.FoodId}）"
                    : "来源身份未知的最终料理";
            var deliveredMessage =
                $"桌 {job.Target.DeskCode + 1} 的目标订单已存在最终料理，手动交接槽已释放；"
                + $"完成来源：{resolution}；expectedResult=0x{(long)job.CurrentResultPointer:X}; "
                + $"actualResult={(servedPointerReadable ? $"0x{(long)servedPointer:X}" : "unavailable")}。"
                + "Mod 未送达、评价、操作托盘、入箱或复位厨具。";
            var completionCode = exactJobResult
                ? OrderPreparationStepCodes.CookingManualHandoffCompleted
                : OrderPreparationStepCodes.CookingManualHandoffResolved;
            var completionReason = exactJobResult
                ? "cooking-manual-handoff-delivered-exact"
                : servedPointerReadable && actualIdentityReadable
                    ? actualIdentity.FoodId == job.Target.FoodId
                        ? "cooking-manual-handoff-equivalent-food"
                        : "cooking-manual-handoff-different-food"
                    : "cooking-manual-handoff-food-identity-unavailable";
            RecordAutomationRuntimeEvent(
                completionCode,
                job,
                deliveredMessage,
                outcome: "completed",
                reasonCode: completionReason,
                terminal: true);
            return (true, deliveredMessage, completionCode);
        }
        catch (Exception ex)
        {
            return HandleManualHandoffReadFailure(job, observedAtUtc, ex.GetBaseException().Message);
        }
    }

    private static (bool Remove, string Message, string Code) HandleManualHandoffReadFailure(
        AutomationCookingJob job,
        DateTime observedAtUtc,
        string diagnostic)
    {
        job.ManualHandoffReadFailureCount++;
        job.ManualHandoffReadFailureClock.Observe(observedAtUtc, eligible: true);
        if (job.ManualHandoffReadFailureCount < ManualHandoffReadFailureLimit
            || job.ManualHandoffReadFailureClock.Elapsed < ManualHandoffReadFailureGrace)
        {
            return (false, "", OrderPreparationStepCodes.CookingPending);
        }

        var message = $"{job.RecipeName} 已进入手动交接，但连续无法确认目标订单料理状态；为避免重复扣料开锅，已停止 job 并保留服务端安全栅栏，请人工确认：{diagnostic}";
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.CookingManualHandoffUnreadable,
            job,
            message,
            outcome: "blocked",
            reasonCode: "cooking-manual-handoff-unreadable",
            terminal: true);
        return (true, message, OrderPreparationStepCodes.CookingManualHandoffUnreadable);
    }

    private static object? ReadCookControllerResult(object cookController, out string invalidResultDiagnostic)
    {
        invalidResultDiagnostic = "";
        if (!TryReadExactMemberValue(
                cookController,
                out var rawResult,
                out var readDiagnostic,
                "Result",
                "<Result>k__BackingField"))
        {
            invalidResultDiagnostic = $"Result 读取失败：{readDiagnostic}";
            return null;
        }

        if (TryAcceptCookControllerFoodResult(
                rawResult,
                "Result",
                out var cookedFood,
                out invalidResultDiagnostic))
        {
            return cookedFood;
        }

        return null;
    }

    private static bool TryAcceptCookControllerFoodResult(
        object? value,
        string source,
        out object? cookedFood,
        out string invalidResultDiagnostic)
    {
        cookedFood = null;
        invalidResultDiagnostic = "";
        if (value == null) return false;

        if (TryReadCookControllerFoodResultIdentity(
                value,
                source,
                out _,
                out invalidResultDiagnostic))
        {
            cookedFood = value;
            return true;
        }

        invalidResultDiagnostic =
            $"{invalidResultDiagnostic}; object={SpecialBusinessDiagnostics.DescribeObject(value)}; managedType={value.GetType().FullName}";
        return false;
    }

    private static bool TryReadCookControllerFoodResultIdentity(
        object value,
        string source,
        out CookControllerFoodResultIdentity identity,
        out string diagnostic)
    {
        identity = default;
        diagnostic = "";
        var managedTypeName = value.GetType().FullName;
        if (!string.Equals(
                managedTypeName,
                CookControllerFoodResultIdentity.ExactManagedTypeName,
                StringComparison.Ordinal))
        {
            diagnostic =
                $"{source} 类型无效：expected={CookControllerFoodResultIdentity.ExactManagedTypeName}; actual={managedTypeName ?? "null"}";
            return false;
        }

        if (!TryReadExactMemberValue(
                value,
                out var rawType,
                out var typeDiagnostic,
                "Type"))
        {
            diagnostic = $"{source}.Type 读取失败：{typeDiagnostic}";
            return false;
        }

        if (!TryReadIntValue(rawType, out var sellableType))
        {
            diagnostic = $"{source}.Type 值无效：managedType={rawType?.GetType().FullName ?? "null"}";
            return false;
        }

        if (!TryReadExactMemberValue(
                value,
                out var rawId,
                out var idDiagnostic,
                "Id"))
        {
            diagnostic = $"{source}.Id 读取失败：{idDiagnostic}";
            return false;
        }

        if (!TryReadIntValue(rawId, out var foodId))
        {
            diagnostic = $"{source}.Id 值无效：managedType={rawId?.GetType().FullName ?? "null"}";
            return false;
        }

        if (!CookControllerFoodResultIdentityPolicy.TryCreate(
                managedTypeName,
                sellableType,
                foodId,
                out identity,
                out var identityDiagnostic))
        {
            diagnostic = $"{source} 身份无效：{identityDiagnostic}";
            return false;
        }

        return true;
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
    /// 当前游戏签名固定为 (int, bool)，第二个参数显式传 false。
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
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(int)
                    && parameters[1].ParameterType == typeof(bool);
            })
            ?? throw new MissingMethodException(RuntimeStorageTypeName, methodName);
        method.Invoke(null, new object?[] { itemId, false });
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
    /// 从当前厨具系统的精确 AllCookers 集合复核前端已预约的控制器。
    /// </summary>
    private static (
        bool Ok,
        bool Waiting,
        object? CookController,
        RuntimeCookerControllerState? ControllerState,
        string Message) TryGetCookerFromCookSystem(
        int recipeCookerType,
        RuntimeCookerReservation reservation)
    {
        if (recipeCookerType <= 0)
        {
            return (false, false, null, null, $"配方厨具类型无效：{recipeCookerType}。");
        }

        var cookSystem = RuntimeCookerReflection.GetCookSystemManager();
        if (cookSystem == null)
        {
            return (false, true, null, null, "当前厨具管理器暂不可用，自动化将等待经营厨具完成初始化。");
        }

        if (!RuntimeCookerReflection.TryReadLockedCookerPositions(
                out var lockedPositions,
                out var lockedStatus))
        {
            return (false, true, null, null,
                $"挑战锁定厨具来源暂时无法读取，自动化将等待状态恢复，不计入失败重试"
                + $"（{lockedStatus}）。");
        }

        if (!RuntimeCookerReflection.TryReadCookerControllerEntriesFromCookSystem(
                cookSystem,
                lockedPositions,
                out var controllerEntries,
                out var controllerStatus))
        {
            return (false, true, null, null,
                $"经营厨具来源暂时无法读取，自动化将等待状态恢复，不计入失败重试"
                + $"（{controllerStatus}；{lockedStatus}）。");
        }

        if (!reservation.TryMatch(
                controllerEntries,
                out var controllerEntry,
                out var reservationError))
        {
            return (false, true, null, null,
                $"预约的厨具控制器已发生身份或位置漂移，自动化不会改选其他厨具，将等待最新快照重新调度"
                + $"（{reservationError}；{controllerStatus}；{lockedStatus}）。");
        }

        if (lockedPositions.Contains(reservation.GridPosition))
        {
            return (false, true, null, null,
                $"预约的厨具控制器 #{reservation.ControllerIndex}/{reservation.ControllerIdentity} "
                + $"在位置 {reservation.GridPosition} 已被挑战机制锁定，"
                + "自动化不会读取该控制器，将等待最新快照重新调度"
                + $"（{controllerStatus}；{lockedStatus}）。");
        }

        var cookController = controllerEntry.Controller;
        if (!RuntimeCookerReflection.TryReadCookerControllerState(
                cookController,
                out var controllerState,
                out var stateStatus))
        {
            return (false, true, null, null,
                $"预约的厨具控制器 #{reservation.ControllerIndex}/{reservation.ControllerIdentity} "
                + "状态无法完整读取，自动化将等待最新快照重新调度"
                + $"（{stateStatus}；{controllerStatus}；{lockedStatus}）。");
        }

        var challengeGate = reservation.EvaluateChallengeGate(
            lockedPositions,
            controllerState.CouldOpen);
        if (challengeGate == RuntimeCookerChallengeGateState.Inconsistent)
        {
            return (false, true, null, null,
                $"预约的厨具控制器 #{reservation.ControllerIndex}/{reservation.ControllerIdentity} "
                + $"在位置 {reservation.GridPosition} 的 LockedCookers 与 CouldCookerOpen 结果互相矛盾，"
                + "自动化将等待同一轮完整状态恢复"
                + $"（{stateStatus}；{controllerStatus}；{lockedStatus}）。");
        }

        if (controllerState.IsEmptyDesk)
        {
            return (false, true, null, null,
                $"预约的厨具控制器 #{reservation.ControllerIndex}/{reservation.ControllerIdentity} "
                + $"在位置 {reservation.GridPosition} 已变为空厨具位，"
                + "自动化不会改选其他厨具，将等待最新快照重新调度"
                + $"（{stateStatus}；{controllerStatus}；{lockedStatus}）。");
        }

        var cookerTypeName = RuntimeCookerReflection.ResolveCookerTypeName(recipeCookerType);
        if (!controllerState.TypeIds.Contains(recipeCookerType))
        {
            return (false, true, null, null,
                $"预约的厨具控制器 #{reservation.ControllerIndex}/{reservation.ControllerIdentity} "
                + "已不再支持配方厨具类型 "
                + $"{cookerTypeName}（{recipeCookerType}），自动化不会改选其他厨具，将等待最新快照重新调度"
                + $"（{stateStatus}；{controllerStatus}；{lockedStatus}）。");
        }

        if (IsCookControllerReserved(cookController))
        {
            return (false, true, null, null,
                $"预约的厨具控制器 #{reservation.ControllerIndex}/{reservation.ControllerIdentity} "
                + "已被另一个 Mod 自动料理任务预约，"
                + "自动化不会改选其他厨具，将等待最新快照重新调度。");
        }

        var startAvailability = RuntimeCookerStartAvailabilityService.Classify(
            cookController,
            controllerState,
            out var startAvailabilityDiagnostic);
        if (startAvailability == AutomationCookerStartAvailability.Unavailable)
        {
            return (false, true, null, null,
                $"预约的厨具控制器 #{reservation.ControllerIndex}/{reservation.ControllerIdentity} "
                + "已不再可用于自动开锅，"
                + "自动化不会改选其他厨具，将等待最新快照重新调度"
                + $"（{stateStatus}；{startAvailabilityDiagnostic}；{controllerStatus}；{lockedStatus}）。");
        }

        return (true, false, cookController, controllerState,
            $"已复核预约厨具控制器 #{reservation.ControllerIndex}/{reservation.ControllerIdentity} "
            + $"@{reservation.GridPosition}，支持 {cookerTypeName}（{recipeCookerType}），"
            + $"startAvailability={startAvailability}"
            + $"（{stateStatus}；{controllerStatus}；{lockedStatus}）。");
    }

    private static bool TryRevalidateCookerBeforeStart(
        RuntimeCookerReservation reservation,
        object cookController,
        object selectedCooker,
        int recipeCookerType,
        out bool waiting,
        out string message)
    {
        waiting = false;
        if (recipeCookerType <= 0)
        {
            message = $"配方厨具类型无效：{recipeCookerType}。";
            return false;
        }

        var current = TryGetCookerFromCookSystem(
            recipeCookerType,
            reservation);
        waiting = current.Waiting;
        if (!current.Ok
            || current.CookController == null
            || current.ControllerState == null)
        {
            message = current.Message;
            return false;
        }

        if (!IsSameObject(cookController, current.CookController))
        {
            waiting = true;
            message = "精确预约的厨具控制器原生对象已发生变化，自动化不会改选其他厨具。";
            return false;
        }

        if (!IsSameObject(selectedCooker, current.ControllerState.Cooker))
        {
            waiting = true;
            message = "精确预约的厨具控制器已经换绑到其他厨具，自动化不会改选其他厨具。";
            return false;
        }

        message = current.Message;
        return true;
    }

    private static bool IsCookControllerReserved(object cookController)
    {
        if (!TryReadNativeObjectPointer(cookController, out var controllerPointer))
        {
            return true;
        }

        lock (AutomationCookingJobLock)
        {
            return AutomationCookingJobs.Any(job =>
                !job.ManualHandoffObserved
                && job.ControllerPointer == controllerPointer);
        }
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
