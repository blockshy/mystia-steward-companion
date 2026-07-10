using System.Text;

namespace MystiaStewardCompanion.Core;

internal static class AtomicFile
{
    public static void WriteAllText(string path, string content, Encoding? encoding = null)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Cannot resolve the parent directory for '{path}'.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content, encoding ?? new UTF8Encoding(false));
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // A cleanup failure must not replace the original write exception.
            }
        }
    }
}
