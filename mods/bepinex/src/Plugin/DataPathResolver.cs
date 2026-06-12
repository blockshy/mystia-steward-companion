using BepInEx;

namespace MystiaStewardCompanion.Plugin;

public static class DataPathResolver
{
    public static string FindDataDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Paths.PluginPath, "mystia-steward-companion", "Data"),
            Path.Combine(Paths.PluginPath, "Data"),
            Path.Combine(Path.GetDirectoryName(typeof(DataPathResolver).Assembly.Location) ?? "", "Data"),
            Path.Combine(Paths.GameRootPath, "BepInEx", "plugins", "mystia-steward-companion", "Data"),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return candidates[0];
    }
}
