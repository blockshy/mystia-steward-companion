using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;

namespace MystiaStewardCompanion.LocalApi;

internal sealed class FavoriteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ManualLogSource _log;

    public FavoriteStore(string path, ManualLogSource log)
    {
        _path = path;
        _log = log;
    }

    public static string ResolvePath()
    {
        return Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", "favorites.json");
    }

    public string GetJson()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(Load(), JsonOptions);
        }
    }

    public string AddRecipe(int customerId, string customerName, string foodTag, int recipeId, IReadOnlyList<int> extraIngredientIds)
    {
        lock (_lock)
        {
            var data = Load();
            var normalizedExtras = NormalizeIds(extraIngredientIds);
            var now = DateTime.UtcNow;
            var existing = data.Recipes.FirstOrDefault(entry =>
                entry.CustomerId == customerId
                && string.Equals(entry.FoodTag, foodTag, StringComparison.Ordinal)
                && entry.RecipeId == recipeId
                && entry.ExtraIngredientIds.SequenceEqual(normalizedExtras));

            if (existing == null)
            {
                data.Recipes.Add(new FavoriteRecipeEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CustomerId = customerId,
                    CustomerName = customerName,
                    FoodTag = foodTag,
                    RecipeId = recipeId,
                    ExtraIngredientIds = normalizedExtras,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
            }
            else
            {
                existing.CustomerName = customerName;
                existing.UpdatedAtUtc = now;
            }

            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    public string RemoveRecipe(string id)
    {
        lock (_lock)
        {
            var data = Load();
            data.Recipes.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    public string AddBeverage(int customerId, string customerName, string beverageTag, int beverageId)
    {
        lock (_lock)
        {
            var data = Load();
            var now = DateTime.UtcNow;
            var existing = data.Beverages.FirstOrDefault(entry =>
                entry.CustomerId == customerId
                && string.Equals(entry.BeverageTag, beverageTag, StringComparison.Ordinal)
                && entry.BeverageId == beverageId);

            if (existing == null)
            {
                data.Beverages.Add(new FavoriteBeverageEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CustomerId = customerId,
                    CustomerName = customerName,
                    BeverageTag = beverageTag,
                    BeverageId = beverageId,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
            }
            else
            {
                existing.CustomerName = customerName;
                existing.UpdatedAtUtc = now;
            }

            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    public string RemoveBeverage(string id)
    {
        lock (_lock)
        {
            var data = Load();
            data.Beverages.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    private FavoriteData Load()
    {
        try
        {
            if (!File.Exists(_path)) return new FavoriteData();
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<FavoriteData>(json, JsonOptions) ?? new FavoriteData();
            data.Version = Math.Max(1, data.Version);
            data.Recipes ??= new List<FavoriteRecipeEntry>();
            data.Beverages ??= new List<FavoriteBeverageEntry>();
            foreach (var entry in data.Recipes)
            {
                entry.ExtraIngredientIds = NormalizeIds(entry.ExtraIngredientIds);
            }

            return data;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Failed to load favorites: {ex.Message}");
            return new FavoriteData();
        }
    }

    private void Save(FavoriteData data)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        data.Version = 1;
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var tempPath = $"{_path}.tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(false));
        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, null);
        }
        else
        {
            File.Move(tempPath, _path);
        }
    }

    private static string BuildMutationJson(bool ok, FavoriteData data, string? error)
    {
        var favoritesJson = JsonSerializer.Serialize(data, JsonOptions);
        return "{\"ok\":"
            + (ok ? "true" : "false")
            + ",\"favorites\":"
            + favoritesJson
            + ",\"error\":"
            + (string.IsNullOrWhiteSpace(error) ? "null" : $"\"{EscapeJson(error)}\"")
            + "}";
    }

    private static List<int> NormalizeIds(IEnumerable<int> ids)
    {
        return ids.Where(id => id >= 0).Distinct().OrderBy(id => id).ToList();
    }

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

internal sealed class FavoriteData
{
    public int Version { get; set; } = 1;
    public List<FavoriteRecipeEntry> Recipes { get; set; } = new();
    public List<FavoriteBeverageEntry> Beverages { get; set; } = new();
}

internal sealed class FavoriteRecipeEntry
{
    public string Id { get; set; } = "";
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public string FoodTag { get; set; } = "";
    public int RecipeId { get; set; }
    public List<int> ExtraIngredientIds { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class FavoriteBeverageEntry
{
    public string Id { get; set; } = "";
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public string BeverageTag { get; set; } = "";
    public int BeverageId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
