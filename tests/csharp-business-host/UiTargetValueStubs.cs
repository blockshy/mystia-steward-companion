// 这里只补齐生产纯值校验的编译依赖，不替代任何颜色、订单身份、目标字段或集合校验。
// 宿主测试使用真实 RuntimeUiTargetSnapshot/Set、RuntimeTargetHighlightStyle 和
// RuntimeOrderTraceIdService；以下数据类型不连接 Unity，也不读取实际游戏订单。
namespace UnityEngine
{
    /// <summary>Unity Color 的最小纯值形状；测试不创建渲染器、不执行游戏图形调用。</summary>
    internal readonly record struct Color(float r, float g, float b, float a);
}

namespace MystiaStewardCompanion.Core
{
    /// <summary>仅满足真实追踪 ID 服务未调用的生成重载所需字段，目标身份校验仍执行生产代码。</summary>
    internal sealed class NightBusinessOrder
    {
        public DateTime? FirstSeenAtUtc { get; init; }
        public long OrderLifecycleSequence { get; init; }
        public int DeskCode { get; init; }
        public int? RuntimeGuestId { get; init; }
        public int? FoodTagId { get; init; }
        public int? BeverageTagId { get; init; }
        public bool IsFreeOrder { get; init; }
    }

    /// <summary>普客追踪 ID 生成重载的纯数据编译依赖；不提供任何运行时反射或对象查找能力。</summary>
    internal sealed class NormalBusinessOrder
    {
        public DateTime? FirstSeenAtUtc { get; init; }
        public long OrderLifecycleSequence { get; init; }
        public string OrderKey { get; init; } = "";
        public int DeskCode { get; init; }
        public string GuestName { get; init; } = "";
        public int FoodId { get; init; }
        public string FoodName { get; init; } = "";
        public int BeverageId { get; init; }
        public string BeverageName { get; init; } = "";
    }
}

namespace MystiaStewardCompanion.Save
{
    /// <summary>特殊订单采集结果的最小托管形状；不链接含游戏对象访问的采集实现。</summary>
    internal sealed class CapturedRuntimeSpecialOrder
    {
        public DateTime? FirstCapturedAt { get; init; }
        public long OrderLifecycleSequence { get; init; }
        public int DeskCode { get; init; }
        public int? GuestId { get; init; }
        public int? FoodTagId { get; init; }
        public int? BeverageTagId { get; init; }
        public bool IsFreeOrder { get; init; }
    }

    /// <summary>普客采集结果只暴露追踪 ID 服务所需的稳定键与生命周期序号。</summary>
    internal sealed class CapturedRuntimeNormalOrder
    {
        public long OrderLifecycleSequence { get; init; }
        public string RuntimeKey { get; init; } = "";
    }
}
