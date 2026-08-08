using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private static bool TryValidateMizuchiRolePair(
        OrderPreparationRequest request,
        SpecialBusinessOrderClassification classification,
        string checkpoint,
        object? order,
        object? controller,
        long orderLifecycleSequence,
        out string diagnostic)
    {
        if (!MizuchiAutomationPolicy.IsAnyRole(request.SpecialBusinessRole)
            && !MizuchiAutomationPolicy.IsAnyRole(classification.Role))
        {
            diagnostic = "";
            return true;
        }

        var accepted = MizuchiAutomationPolicy.TryValidateRolePair(
            request.SpecialBusinessRole,
            classification.Role,
            request.ExtraIngredientIds,
            out var roleDiagnostic);
        if (!classification.AutomationAllowed)
        {
            accepted = false;
            roleDiagnostic = string.IsNullOrWhiteSpace(classification.AutomationBlockReason)
                ? roleDiagnostic
                : $"{roleDiagnostic}; classifier={classification.AutomationBlockReason}";
        }

        nint orderPointer = 0;
        nint controllerPointer = 0;
        if (order != null) TryReadNativeObjectPointer(order, out orderPointer);
        if (controller != null) TryReadNativeObjectPointer(controller, out controllerPointer);
        SpecialBusinessDiagnostics.AppendMizuchiAutomationCheckpoint(
            checkpoint,
            accepted,
            request.SpecialBusinessRole,
            classification.Role,
            request.ExtraIngredientIds,
            actualExtraIngredientIds: null,
            roleDiagnostic,
            orderPointer,
            controllerPointer,
            orderLifecycleSequence);
        diagnostic = roleDiagnostic;
        return accepted;
    }

    private static bool TryValidateMizuchiRuntimeOrder(
        CookingCollectionTarget target,
        RuntimeOrderMatch runtimeOrder,
        string checkpoint,
        out string diagnostic)
    {
        if (!MizuchiAutomationPolicy.IsAnyRole(target.SpecialBusinessRole))
        {
            diagnostic = "";
            return true;
        }

        if (runtimeOrder.Order == null || runtimeOrder.Controller == null)
        {
            diagnostic = $"{checkpoint}: fresh order/controller is unavailable; {runtimeOrder.Diagnostic}";
            AppendMizuchiTargetCheckpoint(target, checkpoint, false, "", null, diagnostic);
            return false;
        }

        if (!TryMatchRuntimeOrderBinding(
                target.OrderBinding,
                runtimeOrder.Order,
                runtimeOrder.Controller,
                out var bindingDiagnostic))
        {
            diagnostic = $"{checkpoint}: exact runtime order binding changed: {bindingDiagnostic}";
            AppendMizuchiTargetCheckpoint(target, checkpoint, false, "", null, diagnostic);
            return false;
        }

        var request = BuildOrderRequestFromCookingTarget(
            target,
            autoDeliverFood: false,
            autoCompleteOrder: false);
        var classification = SpecialBusinessOrderClassifier.Classify(
            runtimeOrder.Order,
            runtimeOrder.Controller,
            $"RuntimeOrderPreparationService.{checkpoint}");
        if (!TryValidateMizuchiRolePair(
                request,
                classification,
                checkpoint,
                runtimeOrder.Order,
                runtimeOrder.Controller,
                runtimeOrder.OrderLifecycleSequence,
                out var roleDiagnostic))
        {
            diagnostic = $"{checkpoint}: {roleDiagnostic}; binding={bindingDiagnostic}";
            return false;
        }

        diagnostic = $"{checkpoint}: {roleDiagnostic}; binding={bindingDiagnostic}";
        return true;
    }

    private static bool TryValidateMizuchiCookingTargetFresh(
        CookingCollectionTarget target,
        string checkpoint,
        out string diagnostic)
    {
        if (!MizuchiAutomationPolicy.IsAnyRole(target.SpecialBusinessRole))
        {
            diagnostic = "";
            return true;
        }

        if (!MizuchiAutomationPolicy.TryValidateRequest(
                target.SpecialBusinessRole,
                target.ExtraIngredientIds,
                out var requestDiagnostic))
        {
            diagnostic = $"{checkpoint}: invalid Mizuchi target: {requestDiagnostic}";
            AppendMizuchiTargetCheckpoint(target, checkpoint, false, "", null, diagnostic);
            return false;
        }

        if (target.Kind != CookingCollectionTargetKind.RareOrder)
        {
            diagnostic = $"{checkpoint}: Mizuchi role is only valid for an exact rare SpecialOrder target";
            AppendMizuchiTargetCheckpoint(target, checkpoint, false, "", null, diagnostic);
            return false;
        }

        RuntimeOrderMatch runtimeOrder;
        try
        {
            var request = BuildOrderRequestFromCookingTarget(
                target,
                autoDeliverFood: false,
                autoCompleteOrder: false);
            runtimeOrder = FindRuntimeOrder(request);
        }
        catch (Exception ex)
        {
            diagnostic = $"{checkpoint}: fresh Mizuchi order lookup failed: {ex.GetBaseException().Message}";
            AppendMizuchiTargetCheckpoint(target, checkpoint, false, "", null, diagnostic);
            return false;
        }

        if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
        {
            diagnostic = $"{checkpoint}: exact Mizuchi order is unavailable: {runtimeOrder.Diagnostic}";
            AppendMizuchiTargetCheckpoint(target, checkpoint, false, "", null, diagnostic);
            return false;
        }

        return TryValidateMizuchiRuntimeOrder(target, runtimeOrder, checkpoint, out diagnostic);
    }

    private static bool TryValidateMizuchiFoodModifier(
        CookingCollectionTarget target,
        object food,
        string checkpoint,
        out string diagnostic)
    {
        if (!MizuchiAutomationPolicy.IsAnyRole(target.SpecialBusinessRole))
        {
            diagnostic = "";
            return true;
        }

        if (!MizuchiAutomationPolicy.TryValidateRequest(
                target.SpecialBusinessRole,
                target.ExtraIngredientIds,
                out var requestDiagnostic))
        {
            diagnostic = $"{checkpoint}: {requestDiagnostic}";
            AppendMizuchiTargetCheckpoint(target, checkpoint, false, "", null, diagnostic);
            return false;
        }

        if (!TryValidateServedFoodExtraIngredients(
                target.ExtraIngredientIds,
                food,
                out var actualExtraIngredientIds,
                out var modifierDiagnostic))
        {
            diagnostic = $"{checkpoint}: {modifierDiagnostic}";
            AppendMizuchiTargetCheckpoint(
                target,
                checkpoint,
                false,
                target.SpecialBusinessRole,
                actualExtraIngredientIds,
                diagnostic);
            return false;
        }

        if (MizuchiAutomationPolicy.IsPossessedRole(target.SpecialBusinessRole)
            && (!MizuchiAutomationPolicy.TryGetTargetIngredientId(
                    target.SpecialBusinessRole,
                    out var targetIngredientId)
                || actualExtraIngredientIds.Count(id => id == targetIngredientId) != 1))
        {
            diagnostic = $"{checkpoint}: possessed result does not contain exactly one target Modifier ingredient";
            AppendMizuchiTargetCheckpoint(
                target,
                checkpoint,
                false,
                target.SpecialBusinessRole,
                actualExtraIngredientIds,
                diagnostic);
            return false;
        }

        diagnostic = $"{checkpoint}: {requestDiagnostic}; {modifierDiagnostic}";
        AppendMizuchiTargetCheckpoint(
            target,
            checkpoint,
            true,
            target.SpecialBusinessRole,
            actualExtraIngredientIds,
            diagnostic);
        return true;
    }

    private static bool TryValidateMizuchiFoodDeliveryPreflight(
        CookingCollectionTarget target,
        RuntimeOrderMatch runtimeOrder,
        object cookedFood,
        string checkpoint,
        out string diagnostic)
    {
        if (!TryValidateMizuchiRuntimeOrder(target, runtimeOrder, checkpoint, out var roleDiagnostic))
        {
            diagnostic = roleDiagnostic;
            return false;
        }

        if (!TryValidateMizuchiFoodModifier(target, cookedFood, checkpoint, out var modifierDiagnostic))
        {
            diagnostic = modifierDiagnostic;
            return false;
        }

        diagnostic = $"{roleDiagnostic}; {modifierDiagnostic}";
        return true;
    }

    private static bool TryValidateMizuchiEvaluationPreflight(
        CookingCollectionTarget target,
        RuntimeOrderMatch runtimeOrder,
        out string diagnostic)
    {
        const string checkpoint = "before-evaluation";
        if (!MizuchiAutomationPolicy.IsAnyRole(target.SpecialBusinessRole))
        {
            diagnostic = "";
            return true;
        }

        if (!TryValidateMizuchiRuntimeOrder(target, runtimeOrder, checkpoint, out var roleDiagnostic))
        {
            diagnostic = roleDiagnostic;
            return false;
        }

        if (!TryReadOrderServedItem(
                runtimeOrder.Order!,
                RuntimeDeliveryItemKind.Food,
                out var servedFood,
                out var servedFoodDiagnostic)
            || servedFood == null)
        {
            diagnostic = $"{checkpoint}: exact served food is unavailable: {servedFoodDiagnostic}";
            AppendMizuchiTargetCheckpoint(target, checkpoint, false, target.SpecialBusinessRole, null, diagnostic);
            return false;
        }

        if (!TryReadCookControllerFoodResultIdentity(
                servedFood,
                "Mizuchi.ServFood",
                out var servedFoodIdentity,
                out var identityDiagnostic)
            || servedFoodIdentity.FoodId != target.FoodId)
        {
            diagnostic = $"{checkpoint}: served food identity mismatch; expected={target.FoodId}; {identityDiagnostic}";
            AppendMizuchiTargetCheckpoint(target, checkpoint, false, target.SpecialBusinessRole, null, diagnostic);
            return false;
        }

        if (!TryValidateMizuchiFoodModifier(target, servedFood, checkpoint, out var modifierDiagnostic))
        {
            diagnostic = modifierDiagnostic;
            return false;
        }

        diagnostic = $"{roleDiagnostic}; foodId={servedFoodIdentity.FoodId}; {modifierDiagnostic}";
        return true;
    }

    private static void AppendMizuchiTargetCheckpoint(
        CookingCollectionTarget target,
        string checkpoint,
        bool accepted,
        string candidateRole,
        IReadOnlyList<int>? actualExtraIngredientIds,
        string detail)
    {
        var binding = target.OrderBinding;
        SpecialBusinessDiagnostics.AppendMizuchiAutomationCheckpoint(
            checkpoint,
            accepted,
            target.SpecialBusinessRole,
            candidateRole,
            target.ExtraIngredientIds,
            actualExtraIngredientIds,
            detail,
            binding?.OrderPointer ?? 0,
            binding?.ControllerPointer ?? 0,
            binding?.LifecycleSequence ?? -1);
    }

    private static (bool Remove, string Message, string Code) BlockMizuchiCookingJob(
        AutomationCookingJob job,
        string checkpoint,
        string detail)
    {
        var message = $"{job.RecipeName} 在瑞灵特殊经营 {checkpoint} 复核中检测到订单角色、评价闭包或成品加料已漂移；"
            + $"未继续送达、入箱或复位厨具，现场已保留并等待人工确认。{detail}";
        job.ControllerLease.Release(
            AutomationCookingControllerLeaseReleaseReason.DeliveryCleanupTerminated,
            DateTime.UtcNow);
        RecordAutomationRuntimeEvent(
            OrderPreparationStepCodes.MizuchiContractMismatch,
            job,
            message,
            actualFoodId: -1,
            outcome: "blocked",
            reasonCode: OrderPreparationStepCodes.MizuchiContractMismatch,
            terminal: true);
        return (true, message, OrderPreparationStepCodes.MizuchiContractMismatch);
    }
}
