using System.Text;

namespace MystiaStewardCompanion.Core;

/// <summary>先写入目标目录内的临时文件，再通过覆盖移动替换目标，避免直接截断原文件。</summary>
/// <remarks>调用方负责并发写入协调；此方法不提供跨进程锁或断电后的持久化保证。</remarks>
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
                // 清理失败不得覆盖原始写入异常，调用方仍需收到导致本次保存失败的原因。
            }
        }
    }
}
