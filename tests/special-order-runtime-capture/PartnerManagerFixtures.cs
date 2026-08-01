namespace NightScene.PartnerUtility;

internal sealed class PartnerManager
{
    internal enum OrderChangeContext
    {
        None,
    }

    public void OnOrderBaseStatusUpdate(
        NightScene.GuestManagementUtility.GuestsManager.OrderBase order,
        OrderChangeContext context,
        int status)
    {
    }

    public void NotifySystemChanged(
        int status,
        OrderChangeContext context,
        NightScene.GuestManagementUtility.GuestsManager.OrderBase order,
        Il2CppSystem.Object payload)
    {
    }
}

internal sealed class InvalidPartnerManager
{
    public void OnOrderBaseStatusUpdate(
        NightScene.GuestManagementUtility.GuestsManager.OrderBase order,
        PartnerManager.OrderChangeContext context,
        long status)
    {
    }

    public void NotifySystemChanged(
        int status,
        PartnerManager.OrderChangeContext context,
        NightScene.GuestManagementUtility.GuestsManager.OrderBase order,
        object payload)
    {
    }
}
