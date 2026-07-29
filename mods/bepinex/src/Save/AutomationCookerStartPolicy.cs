namespace MystiaStewardCompanion.Save;

internal enum AutomationCookerStartAvailability
{
    Unavailable,
    StrictIdle,
    ExtractedResidual,
}

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

        return completedExtractObserved
            ? AutomationCookerStartAvailability.ExtractedResidual
            : AutomationCookerStartAvailability.Unavailable;
    }
}
