// 隔离程序不初始化 Unity；枚举只提供本专项使用的名称。ConfigFile 与 TOML 转换逻辑不是替身。
namespace UnityEngine
{
    public enum KeyCode { None = 0, F8 = 289, F9 = 290, JoystickButton9 = 339 }
}
namespace MystiaStewardCompanion.Save
{
    internal static class AggregateModLogService { public const int DefaultMaxFileCount = 30; }
}
namespace MystiaStewardCompanion.LocalApi
{
    internal sealed class LocalApiConnectionConfigUpdate
    {
        public string? Token { get; init; }
        public string? LanBindHost { get; init; }
    }
    internal sealed class LocalApiLanEndpointDto { public string Address { get; init; } = ""; }
    internal sealed class CompanionDeviceAuthorityException : Exception
    {
        public CompanionDeviceAuthorityException(int status, string message) : base(message) { }
    }
}
