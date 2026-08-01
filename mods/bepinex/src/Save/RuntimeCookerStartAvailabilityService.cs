namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerStartAvailabilityService
{
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
