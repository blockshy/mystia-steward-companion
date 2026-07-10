using System.Text;
using System.Text.Json;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.LocalApi;

internal static class JsonFileStore
{
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
