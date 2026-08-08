namespace MystiaStewardCompanion.Save;

internal static class MizuchiAutomationPolicy
{
    public static bool IsExecutionRole(string? role)
    {
        return TryGetExecutionContract(role, out _);
    }

    public static bool IsAnyRole(string? role)
    {
        return IsExecutionRole(role)
            || string.Equals(
                role,
                SpecialBusinessOrderRoles.MizuchiStoryUnverified,
                StringComparison.Ordinal)
            || string.Equals(
                role,
                SpecialBusinessOrderRoles.MizuchiTrialUnverified,
                StringComparison.Ordinal);
    }

    public static bool TryValidateRequest(
        string? role,
        IReadOnlyList<int>? extraIngredientIds,
        out string diagnostic)
    {
        if (!TryGetExecutionContract(role, out var contract))
        {
            diagnostic = $"unsupported Mizuchi role: {role ?? "<null>"}";
            return false;
        }

        if (extraIngredientIds == null)
        {
            diagnostic = "extra ingredient ids are null";
            return false;
        }

        if (extraIngredientIds.Any(id => id < 0)
            || extraIngredientIds.Distinct().Count() != extraIngredientIds.Count)
        {
            diagnostic = "extra ingredient ids contain an invalid or duplicate value";
            return false;
        }

        if (contract.IsPossessed
            && extraIngredientIds.Count(id => id == contract.TargetIngredientId) != 1)
        {
            diagnostic = $"possessed order requires exactly one Modifier ingredient {contract.TargetIngredientId}";
            return false;
        }

        if (!contract.IsPossessed
            && extraIngredientIds.Contains(contract.TargetIngredientId))
        {
            diagnostic = $"ordinary order forbids Modifier ingredient {contract.TargetIngredientId}";
            return false;
        }

        diagnostic = $"role={role}; extraIngredientIds=[{string.Join(",", extraIngredientIds)}]";
        return true;
    }

    public static bool TryValidateRolePair(
        string? requestRole,
        string? candidateRole,
        IReadOnlyList<int>? extraIngredientIds,
        out string diagnostic)
    {
        if (!IsAnyRole(requestRole) && !IsAnyRole(candidateRole))
        {
            diagnostic = "not a Mizuchi order";
            return true;
        }

        if (!IsExecutionRole(requestRole)
            || !IsExecutionRole(candidateRole)
            || !string.Equals(requestRole, candidateRole, StringComparison.Ordinal))
        {
            diagnostic = $"Mizuchi role mismatch request={requestRole ?? "<null>"}; candidate={candidateRole ?? "<null>"}";
            return false;
        }

        return TryValidateRequest(requestRole, extraIngredientIds, out diagnostic);
    }

    public static bool TryGetTargetIngredientId(string? role, out int targetIngredientId)
    {
        if (TryGetExecutionContract(role, out var contract))
        {
            targetIngredientId = contract.TargetIngredientId;
            return true;
        }

        targetIngredientId = -1;
        return false;
    }

    public static bool IsPossessedRole(string? role)
    {
        return TryGetExecutionContract(role, out var contract) && contract.IsPossessed;
    }

    private static bool TryGetExecutionContract(string? role, out MizuchiRoleContract contract)
    {
        contract = role switch
        {
            SpecialBusinessOrderRoles.MizuchiStoryPossessed => new(
                IsPossessed: true,
                TargetIngredientId: MizuchiConstants.PuyoyoFruitIngredientId),
            SpecialBusinessOrderRoles.MizuchiStoryOrdinary => new(
                IsPossessed: false,
                TargetIngredientId: MizuchiConstants.PuyoyoFruitIngredientId),
            SpecialBusinessOrderRoles.MizuchiTrialPossessed => new(
                IsPossessed: true,
                TargetIngredientId: MizuchiConstants.PepperWaterIngredientId),
            SpecialBusinessOrderRoles.MizuchiTrialOrdinary => new(
                IsPossessed: false,
                TargetIngredientId: MizuchiConstants.PepperWaterIngredientId),
            _ => default,
        };
        return contract.TargetIngredientId > 0;
    }

    private readonly record struct MizuchiRoleContract(
        bool IsPossessed,
        int TargetIngredientId);
}
