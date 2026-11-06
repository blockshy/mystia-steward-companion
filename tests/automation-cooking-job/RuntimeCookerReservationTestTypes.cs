namespace MystiaStewardCompanion.Save;

/// <summary>隔离预约测试使用的格位值；只参与身份比较，不依赖 Unity 坐标对象。</summary>
internal readonly record struct RuntimeCookerGridPosition(int X, int Y, int Z);

/// <summary>提供预约代码所需的最小控制器形状；Controller 仅为测试持有的对象标识。</summary>
internal sealed class RuntimeCookerControllerEntry
{
    public object Controller { get; init; } = null!;
    public RuntimeCookerGridPosition GridPosition { get; init; }
    public string ControllerIdentity { get; init; } = "";
}
