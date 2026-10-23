using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 客人图片身份与订单类型独立。NormalOrder.Guest 的 metadata/#783 返回类型为 GuestBase，
/// 可以承载 SpecialGuest；猯藏分身仍为稀客控制器，不能因为它点具体料理就查普客客群目录。
/// 本值只包含托管数据，不保留原生客人、控制器或 IL2CPP wrapper，也不参与任何执行许可。
/// </summary>
internal sealed record RuntimeGuestImageIdentity(string Kind, int? Id, string Name)
{
    internal static RuntimeGuestImageIdentity Unknown { get; } = new("", null, "");
}

internal static class RuntimeGuestImageIdentityReader
{
    internal const string NormalType = "GameData.Core.Collections.NightSceneUtility.NormalGuest";
    internal const string RareType = "GameData.Core.Collections.NightSceneUtility.SpecialGuest";

    /// <summary>
    /// 对当前订单 Guest 与控制器 OrderingGuest 分别做精确原生转换，随后按对应命名空间核对目录。
    /// 数字相同并不表示同类实体；未知包装、转换冲突、非法 ID 或两个来源不一致均不给图片身份。
    /// 委托由生产反射边界传入，离线测试可验证基类包装语义而不加载 Unity 或执行游戏方法。
    /// </summary>
    internal static RuntimeGuestImageIdentity Resolve(object? guest, object? orderingGuest,
        IReadOnlyDictionary<int, string> normalNames, IReadOnlyDictionary<int, string> rareNames,
        Func<object?, string, object?> cast, Func<object?, string, object?> read)
    {
        try
        {
            var orderIdentity = Read(guest);
            var controllerIdentity = Read(orderingGuest);
            if (guest == null) return controllerIdentity;
            if (orderingGuest == null) return orderIdentity;
            return orderIdentity.Kind.Length > 0 && orderIdentity.Kind == controllerIdentity.Kind
                && orderIdentity.Id == controllerIdentity.Id ? orderIdentity : RuntimeGuestImageIdentity.Unknown;
        }
        catch
        {
            // 图片是只读附属信息；失败保留通用图标，不从名称、ID 区间或订单种类猜另一类图片。
            return RuntimeGuestImageIdentity.Unknown;
        }

        RuntimeGuestImageIdentity Read(object? value)
        {
            if (value == null) return RuntimeGuestImageIdentity.Unknown;
            var normal = cast(value, NormalType);
            var rare = cast(value, RareType);
            if ((normal == null) == (rare == null)) return RuntimeGuestImageIdentity.Unknown;
            var actual = normal ?? rare;
            if (read(actual, "Id") is not int id || id < 0) return RuntimeGuestImageIdentity.Unknown;
            var names = normal != null ? normalNames : rareNames;
            return names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                ? new(normal != null ? "normal-group" : "rare-guest", id, name)
                : RuntimeGuestImageIdentity.Unknown;
        }
    }

    /// <summary>可视来源可以补齐已知身份，但同一订单的两个不同已知实体必须降级，不能取首项。</summary>
    internal static RuntimeGuestImageIdentity Merge(IEnumerable<RuntimeGuestImageIdentity> identities)
    {
        var known = identities.Where(value => (value.Kind is "normal-group" or "rare-guest") && value.Id >= 0)
            .GroupBy(value => (value.Kind, value.Id)).Take(2).ToArray();
        return known.Length == 1 ? known[0].First() : RuntimeGuestImageIdentity.Unknown;
    }

    /// <summary>
    /// 普客喜好仍取已有目录对象，但必须先确认对象确实属于普客客群；复制稀客与未知身份
    /// 不得因恰好同数字 ID 继承另一客群的料理/酒水喜好。该入口不修改目录或任何订单许可。
    /// </summary>
    internal static NormalCustomer? ResolveNormalCustomer(RuntimeGuestImageIdentity identity,
        IReadOnlyDictionary<int, NormalCustomer> customers) => identity.Kind == "normal-group"
        && identity.Id.HasValue && customers.TryGetValue(identity.Id.Value, out var customer) ? customer : null;
}
