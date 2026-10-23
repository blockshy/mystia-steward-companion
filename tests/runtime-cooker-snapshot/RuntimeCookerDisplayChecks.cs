using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;

/// <summary>
/// 仅用与metadata/interop相同的类型和getter形状验证真实读取器，不启动游戏。
/// 重点是读取实例Text、同帧预算、缓存重试、失败隔离及DTO/签名闭环，而非猜测厨具效果。
/// </summary>
internal static class RuntimeCookerDisplayChecks
{
    internal static void Run()
    {
        var reader = new RuntimeCookerDisplayReader();
        var reads = 0;
        var cooker = new GameData.Core.Collections.Cooker(901)
        {
            ReadText = () => { reads++; return new("<b>合成特殊煮锅</b>", "<color=#fff>烹饪时间缩短20%</color><br>这是游戏处理后的效果"); },
        };
        var budget = 2;
        var first = reader.Read(cooker, 0, ref budget);
        Equal(901, first.CookerId, "实例ID不能变成标准类型ID");
        Equal("合成特殊煮锅", first.Name, "必须读取实例Text名称");
        Equal("烹饪时间缩短20%\n这是游戏处理后的效果", first.Effect, "必须保留处理后的效果");
        Equal("ready", first.Status, "完整语言记录应标记就绪");
        Equal(1, reads, "首次只读取一次Text");
        Equal("条件 < 3 且 > 1\n保留<未知>", GameDisplayText.Normalize(
            "<size=12>条件 < 3 且 > 1</size><BR/>保留<未知>"), "只清理游戏样式，不能吞掉比较符及未知文本");
        reader.Read(cooker, 29_999, ref budget);
        Equal(1, reads, "缓存命中不应再次访问原生Text");
        Equal(1, budget, "缓存命中不消耗原生文本预算");

        cooker.ReadText = () => { reads++; return new("更新名称", "更新效果"); };
        budget = 0;
        Equal(first, reader.Read(cooker, 30_000, ref budget), "批量到期时应保留同ID旧文字并服从预算");
        Equal(1, reads, "无预算不能读取原生文本");
        budget = 2;
        Equal("更新效果", reader.Read(cooker, 30_000, ref budget).Effect, "下一快照预算恢复后刷新实际说明");

        var pendingReads = 0;
        var pending = new GameData.Core.Collections.Cooker(902)
        {
            ReadText = () => { pendingReads++; throw new InvalidOperationException("合成目录尚未就绪"); },
        };
        Equal("unavailable", reader.Read(pending, 30_000, ref budget).Status, "语言异常应降级为未知");
        Equal(0, budget, "同一快照最多执行两次文本读取");
        var deferredReads = 0;
        var deferred = new GameData.Core.Collections.Cooker(903)
        {
            ReadText = () => { deferredReads++; return new("下一件", ""); },
        };
        Equal("unavailable", reader.Read(deferred, 30_000, ref budget).Status, "预算不足不能伪造说明就绪");
        Equal(0, deferredReads, "第三件应延后到下一次快照");
        budget = 2;
        reader.Read(pending, 34_999, ref budget);
        Equal(1, pendingReads, "失败不能每帧重试");
        pending.ReadText = () => { pendingReads++; return new("已就绪", ""); };
        var empty = reader.Read(pending, 35_000, ref budget);
        Equal("ready", empty.Status, "空说明仍可来自已就绪的游戏记录");
        Equal("", empty.Effect, "不能把空原文编造成没有特殊效果");
        Equal("下一件", reader.Read(deferred, 35_000, ref budget).Name, "被预算推迟的记录可以恢复");
        Equal(0, budget, "恢复读取也受相同预算限制");
        budget = 2;
        Equal("unavailable", reader.Read(new object(), 40_000, ref budget).Status, "不能接受同名但非精确游戏对象");
        Equal(2, budget, "结构不符不进入原生文本读取");

        // 覆盖所有不可用分支，空闲与已确认取餐残留不能残留旧原因。
        foreach (var (phase, result, chosen, open, expected) in new[]
        {
            (1, false, true, true, "loaded"), (2, false, true, true, "cooking"),
            (3, true, true, true, "finished"), (0, true, false, true, "result-pending"),
            (2, false, true, false, "cooking"), (3, true, true, false, "finished"),
            (0, false, true, true, "residual-unconfirmed"), (0, false, false, false, "closed"),
            (-1, false, false, true, "unknown"),
        })
        {
            var state = new RuntimeCookerControllerState { Phase = phase, CouldOpen = open,
                TypeIds = new[] { 1 }, Result = result ? new object() : null,
                ChosenRecipe = chosen ? new object() : null };
            Equal(expected, RuntimeCookerStartAvailabilityService.DescribeUnavailable(state,
                AutomationCookerStartAvailability.Unavailable), "不可用原因必须对应同次事实");
            Equal("", RuntimeCookerStartAvailabilityService.DescribeUnavailable(state,
                AutomationCookerStartAvailability.StrictIdle), "空闲不得保留旧原因");
        }

        // 通过生产快照和DTO验证：说明缺失不撤销完整快照，名称/效果不会改变原准入。
        var controller = new object();
        RuntimeCookerReflection.ManagerReader = () => new object();
        RuntimeCookerReflection.ControllerEntryReader = _ => (true,
            new[] { new RuntimeCookerControllerEntry { Controller = controller,
                GridPosition = new(0, 0, 0), ControllerIdentity = "0xdisplay" } }, "ok");
        RuntimeCookerReflection.LockedPositionReader = () => (true,
            new HashSet<RuntimeCookerGridPosition>(), "ok");
        RuntimeCookerReflection.StateReader = _ => (true,
            new RuntimeCookerControllerState { Cooker = cooker, TypeIds = new[] { 1 },
                CouldOpen = true, Phase = 2, ChosenRecipe = new object() }, "ok");
        var snapshot = new RecommendationState();
        RuntimeCookerSnapshotService.ApplyTo(snapshot);
        Equal(true, snapshot.PlacedCookerSnapshotComplete, "附加展示不改变完整性");
        var item = snapshot.PlacedCookers.Single();
        Equal(false, item.AutomationAvailable, "制作中厨具仍然不可预约");
        Equal("煮锅", item.Name, "规范能力名称不可被真实实例名覆盖");
        Equal("更新名称", item.DisplayName, "真实实例名称应独立发布");
        Equal("cooking", item.UnavailableReason, "快照必须交付具体原因");
        var dto = RecommendationStateSnapshot.From(snapshot).PlacedCookers.Single();
        Equal(item.CookerId, dto.CookerId, "DTO必须保留实例身份");
        Equal(item.DisplayName, dto.DisplayName, "DTO必须保留实际实例名");
        Equal(item.Effect, dto.Effect, "DTO必须保留真实效果");
        Equal(item.MetadataStatus, dto.MetadataStatus, "DTO必须保留读取状态");
        Equal(item.UnavailableReason, dto.UnavailableReason, "DTO必须保留不可用原因");
        var before = RuntimeCookerSnapshotContentSignature.Append(17, snapshot);
        foreach (var changed in new[] { "id", "name", "effect", "metadata", "reason" })
        {
            snapshot.PlacedCookers.Clear();
            snapshot.PlacedCookers.Add(new PlacedCookerInfo {
                CookerId = changed == "id" ? item.CookerId + 1 : item.CookerId,
                Name = item.Name, DisplayName = item.DisplayName + (changed == "name" ? "变化" : ""),
                Effect = item.Effect + (changed == "effect" ? "变化" : ""),
                MetadataStatus = changed == "metadata" ? "unavailable" : item.MetadataStatus,
                ControllerIndex = item.ControllerIndex, ControllerIdentity = item.ControllerIdentity,
                GridPosition = item.GridPosition, TypeIds = item.TypeIds, TypeNames = item.TypeNames,
                CouldOpen = item.CouldOpen, AutomationAvailable = item.AutomationAvailable,
                AutomationAvailability = item.AutomationAvailability,
                UnavailableReason = changed == "reason" ? "finished" : item.UnavailableReason,
                AutomationAvailabilityDiagnostic = item.AutomationAvailabilityDiagnostic, Source = item.Source });
            if (before == RuntimeCookerSnapshotContentSignature.Append(17, snapshot))
                throw new InvalidOperationException($"厨具展示字段{changed}变化不能被快照内容签名吞掉。");
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}；expected={expected}, actual={actual}");
    }
}

// 下列隔离类型只模拟已核验getter形状；不链接Unity、不执行实际游戏语言逻辑。
namespace GameData.Core
{
    public class NonTradableObjectBase
    {
        public int Id { get; }
        public Func<CoreLanguage.ObjectLanguageBase> ReadText { get; set; } = () => new("", "");
        public virtual CoreLanguage.ObjectLanguageBase Text => ReadText();
        public NonTradableObjectBase(int id) { Id = id; }
    }
}
namespace GameData.Core.Collections
{
    public sealed class Cooker : NonTradableObjectBase
    {
        public Cooker(int id) : base(id) { }
    }
}
namespace GameData.CoreLanguage
{
    public class LanguageBase
    {
        public string Name { get; }
        public string Description { get; }
        public LanguageBase(string name, string description) { Name = name; Description = description; }
    }
    public sealed class ObjectLanguageBase : LanguageBase
    {
        public ObjectLanguageBase(string name, string description) : base(name, description) { }
    }
}
