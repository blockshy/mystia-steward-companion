using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;

namespace MystiaStewardCompanion.LocalApi;

internal sealed class CustomRecipeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly FavoriteStore _favoriteStore;
    private readonly ManualLogSource _log;
    private bool _migrationChecked;

    public CustomRecipeStore(string path, FavoriteStore favoriteStore, ManualLogSource log)
    {
        _path = path;
        _favoriteStore = favoriteStore;
        _log = log;
    }

    public static string ResolvePath()
    {
        return Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", "custom-recipes.json");
    }

    public string GetJson()
    {
        lock (_lock)
        {
            var data = LoadWithMigration();
            Save(data);
            return JsonSerializer.Serialize(data, JsonOptions);
        }
    }

    public string Upsert(CustomRecipeMutation mutation)
    {
        lock (_lock)
        {
            var data = LoadWithMigration();
            var now = DateTime.UtcNow;
            var normalizedExtras = NormalizeIds(mutation.ExtraIngredientIds);
            var existing = string.IsNullOrWhiteSpace(mutation.Id)
                ? null
                : data.Recipes.FirstOrDefault(entry => string.Equals(entry.Id, mutation.Id, StringComparison.Ordinal));

            if (existing == null)
            {
                data.Recipes.Add(new CustomRecipeEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CustomerId = mutation.CustomerId,
                    CustomerName = mutation.CustomerName.Trim(),
                    FoodTag = NormalizeOptionalTag(mutation.FoodTag),
                    FoodId = mutation.FoodId,
                    RecipeId = mutation.RecipeId,
                    RecipeName = mutation.RecipeName.Trim(),
                    ExtraIngredientIds = normalizedExtras,
                    Enabled = mutation.Enabled,
                    PinToTop = mutation.PinToTop,
                    SortOrder = mutation.SortOrder ?? NextSortOrder(data),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
            }
            else
            {
                existing.CustomerId = mutation.CustomerId;
                existing.CustomerName = mutation.CustomerName.Trim();
                existing.FoodTag = NormalizeOptionalTag(mutation.FoodTag);
                existing.FoodId = mutation.FoodId;
                existing.RecipeId = mutation.RecipeId;
                existing.RecipeName = mutation.RecipeName.Trim();
                existing.ExtraIngredientIds = normalizedExtras;
                existing.Enabled = mutation.Enabled;
                existing.PinToTop = mutation.PinToTop;
                if (mutation.SortOrder != null) existing.SortOrder = mutation.SortOrder.Value;
                existing.UpdatedAtUtc = now;
            }

            NormalizeData(data);
            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    public string Remove(string id)
    {
        lock (_lock)
        {
            var data = LoadWithMigration();
            data.Recipes.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            NormalizeData(data);
            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    public string Toggle(string id, bool enabled)
    {
        lock (_lock)
        {
            var data = LoadWithMigration();
            var entry = data.Recipes.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (entry != null)
            {
                entry.Enabled = enabled;
                entry.UpdatedAtUtc = DateTime.UtcNow;
            }

            NormalizeData(data);
            Save(data);
            return BuildMutationJson(entry != null, data, entry == null ? "custom recipe not found" : null);
        }
    }

    public string Move(string id, string direction)
    {
        lock (_lock)
        {
            var data = LoadWithMigration();
            var ordered = data.Recipes
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.CreatedAtUtc)
                .ToList();
            var index = ordered.FindIndex(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return BuildMutationJson(false, data, "custom recipe not found");
            }

            var targetIndex = string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase)
                ? index - 1
                : index + 1;
            if (targetIndex >= 0 && targetIndex < ordered.Count)
            {
                (ordered[index].SortOrder, ordered[targetIndex].SortOrder) = (ordered[targetIndex].SortOrder, ordered[index].SortOrder);
                ordered[index].UpdatedAtUtc = DateTime.UtcNow;
                ordered[targetIndex].UpdatedAtUtc = DateTime.UtcNow;
            }

            NormalizeData(data);
            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    private CustomRecipeData LoadWithMigration()
    {
        var data = Load();
        if (_migrationChecked) return data;

        _migrationChecked = true;
        var migrated = _favoriteStore.ExtractManualRecipeFavorites();
        if (migrated.Count == 0) return data;

        var now = DateTime.UtcNow;
        var existingKeys = new HashSet<string>(
            data.Recipes.Select(entry => BuildRecipeKey(entry.CustomerId, entry.FoodTag, entry.FoodId, entry.ExtraIngredientIds)),
            StringComparer.Ordinal);
        var nextSortOrder = NextSortOrder(data);

        foreach (var favorite in migrated)
        {
            var key = BuildRecipeKey(favorite.CustomerId, favorite.FoodTag, favorite.RecipeId, favorite.ExtraIngredientIds);
            if (existingKeys.Contains(key)) continue;
            existingKeys.Add(key);
            data.Recipes.Add(new CustomRecipeEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                CustomerId = favorite.CustomerId,
                CustomerName = favorite.CustomerName,
                FoodTag = NormalizeOptionalTag(favorite.FoodTag),
                FoodId = favorite.RecipeId,
                RecipeId = -1,
                RecipeName = "",
                ExtraIngredientIds = NormalizeIds(favorite.ExtraIngredientIds),
                Enabled = true,
                PinToTop = true,
                SortOrder = nextSortOrder,
                CreatedAtUtc = favorite.CreatedAtUtc == default ? now : favorite.CreatedAtUtc,
                UpdatedAtUtc = now,
            });
            nextSortOrder += 100;
        }

        NormalizeData(data);
        Save(data);
        _log.LogInfo($"Migrated {migrated.Count} manual recipe favorites to custom-recipes.json.");
        return data;
    }

    private CustomRecipeData Load()
    {
        try
        {
            if (!File.Exists(_path)) return new CustomRecipeData();
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<CustomRecipeData>(json, JsonOptions) ?? new CustomRecipeData();
            NormalizeData(data);
            return data;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Failed to load custom recipes: {ex.Message}");
            return new CustomRecipeData();
        }
    }

    private void Save(CustomRecipeData data)
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

    private static void NormalizeData(CustomRecipeData data)
    {
        data.Version = Math.Max(1, data.Version);
        data.Recipes ??= new List<CustomRecipeEntry>();
        var nextSortOrder = 100;
        foreach (var entry in data.Recipes.OrderBy(entry => entry.SortOrder).ThenBy(entry => entry.CreatedAtUtc))
        {
            entry.CustomerName = entry.CustomerName.Trim();
            entry.FoodTag = NormalizeOptionalTag(entry.FoodTag);
            entry.RecipeName = entry.RecipeName.Trim();
            entry.ExtraIngredientIds = NormalizeIds(entry.ExtraIngredientIds);
            if (entry.SortOrder <= 0) entry.SortOrder = nextSortOrder;
            nextSortOrder = Math.Max(nextSortOrder + 100, entry.SortOrder + 100);
        }
    }

    private static int NextSortOrder(CustomRecipeData data)
    {
        return data.Recipes.Count == 0 ? 100 : data.Recipes.Max(entry => entry.SortOrder) + 100;
    }

    private static string BuildMutationJson(bool ok, CustomRecipeData data, string? error)
    {
        var customRecipesJson = JsonSerializer.Serialize(data, JsonOptions);
        return "{\"ok\":"
            + (ok ? "true" : "false")
            + ",\"customRecipes\":"
            + customRecipesJson
            + ",\"error\":"
            + (string.IsNullOrWhiteSpace(error) ? "null" : $"\"{EscapeJson(error)}\"")
            + "}";
    }

    private static string BuildRecipeKey(int customerId, string? foodTag, int foodId, IEnumerable<int> extraIngredientIds)
    {
        return $"{customerId}:{NormalizeOptionalTag(foodTag) ?? "*"}:{foodId}:{string.Join(",", NormalizeIds(extraIngredientIds))}";
    }

    private static string? NormalizeOptionalTag(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static List<int> NormalizeIds(IEnumerable<int>? ids)
    {
        return (ids ?? Array.Empty<int>())
            .Where(id => id >= 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
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

internal sealed class CustomRecipeMutation
{
    public string Id { get; init; } = "";
    public int CustomerId { get; init; }
    public string CustomerName { get; init; } = "";
    public string? FoodTag { get; init; }
    public int FoodId { get; init; }
    public int RecipeId { get; init; }
    public string RecipeName { get; init; } = "";
    public IReadOnlyList<int> ExtraIngredientIds { get; init; } = Array.Empty<int>();
    public bool Enabled { get; init; }
    public bool PinToTop { get; init; }
    public int? SortOrder { get; init; }
}

internal sealed class CustomRecipeData
{
    public int Version { get; set; } = 1;
    public List<CustomRecipeEntry> Recipes { get; set; } = new();
}

internal sealed class CustomRecipeEntry
{
    public string Id { get; set; } = "";
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public string? FoodTag { get; set; }
    public int FoodId { get; set; }
    public int RecipeId { get; set; }
    public string RecipeName { get; set; } = "";
    public List<int> ExtraIngredientIds { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public bool PinToTop { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
