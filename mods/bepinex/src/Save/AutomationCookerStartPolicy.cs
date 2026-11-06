namespace MystiaStewardCompanion.Save;

internal enum AutomationCookerStartAvailability
{
    Unavailable,
    StrictIdle,
    ExtractedResidual,
}

/// <summary>根据已采集的厨具事实区分严格空闲与取餐后残留选中配方，避免仅凭阶段值允许开锅。</summary>
internal static class AutomationCookerStartPolicy
{
    public static AutomationCookerStartAvailability Classify(
        int phase,
        bool resultEmpty,
        bool chosenRecipeEmpty,
        bool couldOpen,
        bool completedExtractObserved)
    {
        if (phase != 0 || !resultEmpty || !couldOpen)
        {
            return AutomationCookerStartAvailability.Unavailable;
        }

        if (chosenRecipeEmpty)
        {
            return AutomationCookerStartAvailability.StrictIdle;
        }

        // 选中配方仍在时，必须有已完成取餐的观测证据；不能把未知残留当作可覆盖的空闲状态。
        return completedExtractObserved
            ? AutomationCookerStartAvailability.ExtractedResidual
            : AutomationCookerStartAvailability.Unavailable;
    }
}
