using System.Text.Json;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>将目录 JSON 与其摘要绑定在同一值中，保证响应内容和缓存验证标识来自同一次序列化。</summary>
internal readonly record struct LocalApiRuntimeDataPayload(string Json, string Signature)
{
    public static LocalApiRuntimeDataPayload Create(
        object catalog,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);

        var json = JsonSerializer.Serialize(catalog, catalog.GetType(), options);
        return new LocalApiRuntimeDataPayload(
            json,
            LocalApiSnapshotSignature.Compute(json));
    }
}
