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
            var data = Load();
            return JsonSerializer.Serialize(data, JsonOptions);
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
            var data = JsonFileStore.LoadOrCreate<FavoriteData>(_path, JsonOptions);
            if (data.Version != 1)
            {
                throw new InvalidDataException($"Unsupported favorites schema version: {data.Version}.");
            }

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
            _log.LogError($"Failed to load favorites from '{_path}': {ex.Message}");
            throw new InvalidDataException("The favorites file could not be read. The original file was not changed.", ex);
        }
    }

    private void Save(FavoriteData data)
    {
        data.Version = 1;
        JsonFileStore.Save(_path, data, JsonOptions);
    }

    private static string BuildMutationJson(bool ok, FavoriteData data, string? error)
    {
        return JsonSerializer.Serialize(new LocalApiFavoriteMutationDto
        {
            Ok = ok,
            Favorites = data,
            Error = string.IsNullOrWhiteSpace(error) ? null : error,
        }, JsonOptions);
    }

    private static List<int> NormalizeIds(IEnumerable<int>? ids)
    {
        return (ids ?? Array.Empty<int>())
            .Where(id => id >= 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }
}

internal sealed class FavoriteData
{
    public int Version { get; set; } = 1;
    public List<FavoriteRecipeEntry> Recipes { get; set; } = new();
    public List<FavoriteBeverageEntry> Beverages { get; set; } = new();
}

internal sealed class LocalApiFavoriteMutationDto
{
    public bool Ok { get; init; }
    public FavoriteData Favorites { get; init; } = new();
    public string? Error { get; init; }
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
