namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerStartAvailabilityService
{
    /// <summary>
    /// 将同次只读快照映射为稳定原因码；不再读取游戏对象、不解析诊断字符串，
    /// 更不以展示原因代替 Classify 的准入。CookPhase 的0..3经metadata/interop确认
    /// 分别为 Idle、Loaded、Cooking、Finished。未知残留不能误报为“空闲”。
    /// </summary>
    internal static string DescribeUnavailable(RuntimeCookerControllerState state,
        AutomationCookerStartAvailability availability)
    {
        if (availability != AutomationCookerStartAvailability.Unavailable) return "";
        if (state.IsEmptyDesk || state.TypeIds.Count == 0) return "no-cooker";
        // Native CouldCookerOpen只检查EventManager与LockedCookers，不等于锅次阶段。
        // 展示优先陈述已知锅次事实，避免额外锁定状态掩盖“制作中/待取餐”。
        return state.Phase switch
        {
            1 => "loaded",
            2 => "cooking",
            3 => "finished",
            0 when !state.ResultEmpty => "result-pending",
            0 when !state.CouldOpen => "closed",
            0 when !state.ChosenRecipeEmpty => "residual-unconfirmed",
            _ => "unknown",
        };
    }

    public static AutomationCookerStartAvailability Classify(
        object cookController,
        RuntimeCookerControllerState state,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(cookController);
        ArgumentNullException.ThrowIfNull(state);

        if (state.IsEmptyDesk || state.TypeIds.Count == 0)
        {
            diagnostic = $"startAvailability={AutomationCookerStartAvailability.Unavailable}; "
                + $"emptyDesk={state.IsEmptyDesk}; capabilityCount={state.TypeIds.Count}";
            return AutomationCookerStartAvailability.Unavailable;
        }

        var completedExtractObserved = false;
        var ownershipDiagnostic = "ownership=not-required";
        if (state.Phase == 0
            && state.ResultEmpty
            && !state.ChosenRecipeEmpty
            && state.CouldOpen)
        {
            var ownershipReadable = RuntimeCookingGenerationTracker.TryGetOwnershipSnapshot(
                cookController,
                out var ownershipSnapshot,
                out ownershipDiagnostic);
            completedExtractObserved = ownershipReadable
                && ownershipSnapshot.LastMutation == RuntimeCookingContentMutation.Extract
                && ownershipSnapshot.MutationCompleted;
        }

        var availability = AutomationCookerStartPolicy.Classify(
            state.Phase,
            state.ResultEmpty,
            state.ChosenRecipeEmpty,
            state.CouldOpen,
            completedExtractObserved);
        diagnostic = $"startAvailability={availability}; {ownershipDiagnostic}";
        return availability;
    }
}
