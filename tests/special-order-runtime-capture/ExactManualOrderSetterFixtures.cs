namespace NightScene.GuestManagementUtility;

internal sealed class GuestGroupController
{
    internal enum EvaluationResult
    {
        None,
    }
}

internal sealed class GuestsManager
{
    internal sealed class OrderBase
    {
    }

    private void SetManualControllerOrderInternal(
        GuestGroupController controller,
        Il2CppSystem.Action<GuestGroupController.EvaluationResult> onEvaluate,
        OrderBase order)
    {
    }
}

internal sealed class InvalidGuestsManager
{
    private void SetManualControllerOrderInternal(
        GuestGroupController controller,
        Il2CppSystem.Action onEvaluate,
        GuestsManager.OrderBase order)
    {
    }
}
