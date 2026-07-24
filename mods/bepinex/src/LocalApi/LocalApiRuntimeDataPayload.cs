using System.Text.Json;

namespace MystiaStewardCompanion.LocalApi;

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
