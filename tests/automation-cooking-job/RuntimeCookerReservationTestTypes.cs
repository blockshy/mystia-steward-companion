namespace MystiaStewardCompanion.Save;

internal readonly record struct RuntimeCookerGridPosition(int X, int Y, int Z);

internal sealed class RuntimeCookerControllerEntry
{
    public object Controller { get; init; } = null!;
    public RuntimeCookerGridPosition GridPosition { get; init; }
    public string ControllerIdentity { get; init; } = "";
}
