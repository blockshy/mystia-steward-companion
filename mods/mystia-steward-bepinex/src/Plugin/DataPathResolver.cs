using BepInEx;

namespace MystiaSteward.Plugin;

public static class DataPathResolver
{
    public static string FindDataDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Paths.PluginPath, "MystiaSteward", "Data"),
            Path.Combine(Paths.PluginPath, "Data"),
            Path.Combine(Path.GetDirectoryName(typeof(DataPathResolver).Assembly.Location) ?? "", "Data"),
            Path.Combine(Paths.GameRootPath, "BepInEx", "plugins", "MystiaSteward", "Data"),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        return candidates[0];
    }
}
