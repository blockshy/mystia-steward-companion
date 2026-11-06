namespace NightScene.PartnerUtility;

/// <summary>仅提供伙伴通知方法的精确签名，供补丁目标筛选测试进行反射检查。</summary>
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

/// <summary>
/// 使用 long 状态和 CLR object 载荷构造近似签名；名称相同不能绕过参数类型校验。
/// 空方法体不模拟伙伴系统行为。
/// </summary>
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
