using System.Diagnostics;

namespace MystiaStewardCompanion.Plugin;

/// <summary>
/// 为独立管家和更新器创建局部启动环境，避免将游戏的 Steam 关联身份传给工具窗口。
/// 所有修改仅作用于 ProcessStartInfo 的环境副本，绝不临时改写游戏进程的全局环境。
/// </summary>
/// <remarks>
/// Steam 官方说明叠加层会自动挂接从 Steam 启动的图形程序：
/// https://partner.steamgames.com/doc/features/overlay 。下列 NoOverlayUIDrawing 名称可在当前客户端
/// 二进制中确认，但并非 Steamworks 保证稳定的公开接口；不能据此声称 DLL 不会加载，
/// 或替代实际 Steam 开启性能监视器时的验证。此处不操作注入模块、不改 Steam/游戏配置。
/// </remarks>
internal static class CompanionProcessEnvironment
{
    private static readonly HashSet<string> GameIdentityVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "SteamAppId", "SteamGameId", "SteamOverlayGameId",
    };

    private static readonly HashSet<string> OverlayPreferenceVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "SteamNoOverlayUIDrawing",
    };

    /// <summary>
    /// 保留既有工作目录、参数传递与系统运行环境，只隔离与游戏叠加层相关的少量标记。
    /// 必须直接创建进程，不能切换 ShellExecute 绕过此环境块；调用者仍自行设置 ArgumentList。
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string executablePath, string workingDirectory,
        bool createNoWindow = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = createNoWindow,
        };
        Apply(startInfo);
        return startInfo;
    }

    /// <summary>
    /// 幂等地处理当前启动描述。枚举副本并显式忽略大小写，也覆盖调用方提供的大小写变体；
    /// 不清空 PATH、SystemRoot、TEMP、用户目录、代理等无关字段，不读取或记录任何字段值。
    /// </summary>
    internal static void Apply(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute)
            throw new InvalidOperationException("Companion process environment requires direct process creation.");

        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (GameIdentityVariables.Contains(key) || OverlayPreferenceVariables.Contains(key))
                startInfo.Environment.Remove(key);
        }

        // 双重限定：取消继承游戏身份，并请求本工具窗口不绘制 Steam 叠加界面。
        // 即使某版 Steam 忽略这些偏好，失败范围也只是此子进程，不影响父游戏的叠加层设置。
        startInfo.Environment["SteamNoOverlayUIDrawing"] = "1";
    }
}
