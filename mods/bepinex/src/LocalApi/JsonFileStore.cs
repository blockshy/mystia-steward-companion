using System.Text;
using System.Text.Json;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>为本地 JSON 配置提供严格读取及替换式保存，不吞掉解析、权限或磁盘错误。</summary>
internal static class JsonFileStore
{
    /// <summary>仅在文件不存在时返回内存默认值；不立即创建文件，损坏文件和 null 文档均报告失败。</summary>
    public static T LoadOrCreate<T>(string path, JsonSerializerOptions options)
        where T : class, new()
    {
        if (!File.Exists(path)) return new T();

        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<T>(json, options)
            ?? throw new InvalidDataException($"JSON file '{path}' contains a null document.");
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions options)
    {
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, options));
    }
}
