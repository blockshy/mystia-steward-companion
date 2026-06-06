using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Logging;

namespace MystiaSteward.Plugin;

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
