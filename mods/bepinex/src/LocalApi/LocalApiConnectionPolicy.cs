namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 连接表单与启动凭据共用的纯值校验。Token 经 HTTP Header 与 ArgumentList 传递，
/// 允许可见 ASCII 标点，但拒绝空白、控制字符及不能由现有 HTTP 解析器无损表达的字符。
/// 不修剪用户输入，否则回执、持久化与下一次启动可能使用不同凭据。
/// </summary>
internal static class LocalApiConnectionPolicy
{
    public static bool IsUsableToken(string? token) => token is { Length: >= 8 and <= 128 }
        && token.All(character => character is >= '!' and <= '~');

    public static void Validate(LocalApiConnectionConfigUpdate update, IReadOnlyList<LocalApiLanEndpointDto> available)
    {
        if (update.Token != null && !IsUsableToken(update.Token))
            throw new CompanionDeviceAuthorityException(400, "连接密码须为 8–128 位非空白 ASCII 字符。");
        if (update.LanBindHost != null && update.LanBindHost != "auto"
            && !available.Any(item => string.Equals(item.Address, update.LanBindHost, StringComparison.Ordinal)))
            throw new CompanionDeviceAuthorityException(400, "所选网卡地址已不可用，请刷新并重新选择。");
    }
}
