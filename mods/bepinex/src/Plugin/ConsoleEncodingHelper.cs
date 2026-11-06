using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;

namespace MystiaStewardCompanion.Plugin;

/// <summary>尽力统一托管控制台与 Windows 控制台代码页，降低中文日志乱码的概率。</summary>
/// <remarks>编码设置失败只记录警告，不应阻止插件加载；Win32 返回值未作为成功确认。</remarks>
internal static class ConsoleEncodingHelper
{
    private const uint Utf8CodePage = 65001;

    public static void TryUseUtf8(ManualLogSource log)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                SetConsoleOutputCP(Utf8CodePage);
                SetConsoleCP(Utf8CodePage);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning($"Failed to set console UTF-8 encoding: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint wCodePageID);
}
