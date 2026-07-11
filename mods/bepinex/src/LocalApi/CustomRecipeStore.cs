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
            var data = Load();
            return JsonSerializer.Serialize(data, JsonOptions);
        }
    }

    public string SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            var data = Load();
            if (data.Enabled != enabled)
            {
                data.Enabled = enabled;
                Save(data);
            }

            return BuildMutationJson(true, data, null);
        }
    }

    public string Upsert(CustomRecipeMutation mutation)
    {
        lock (_lock)
        {
            var data = Load();
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
                    Enabled = mutation.Enabled ?? true,
                    PinToTop = mutation.PinToTop ?? true,
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
                if (mutation.Enabled != null) existing.Enabled = mutation.Enabled.Value;
                if (mutation.PinToTop != null) existing.PinToTop = mutation.PinToTop.Value;
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
            var data = Load();
            data.Recipes.RemoveAll(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            NormalizeData(data);
            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    public string UpdateFlags(
        CustomRecipeSelection selection,
        bool? enabled,
        bool? pinToTop)
    {
        lock (_lock)
        {
            var data = Load();
            if (enabled == null && pinToTop == null)
            {
                return BuildMutationJson(false, data, "no custom recipe flags specified");
            }

            var entries = SelectEntries(data, selection);
            if (entries.Count == 0)
            {
                return BuildMutationJson(false, data, "custom recipe selection matched no entries");
            }

            var now = DateTime.UtcNow;
            var changed = false;
            foreach (var entry in entries)
            {
                var entryChanged = false;
                if (enabled != null && entry.Enabled != enabled.Value)
                {
                    entry.Enabled = enabled.Value;
                    entryChanged = true;
                }
                if (pinToTop != null && entry.PinToTop != pinToTop.Value)
                {
                    entry.PinToTop = pinToTop.Value;
                    entryChanged = true;
                }
                if (!entryChanged) continue;
                entry.UpdatedAtUtc = now;
                changed = true;
            }

            if (changed)
            {
                NormalizeData(data);
                Save(data);
            }
            return BuildMutationJson(true, data, null);
        }
    }

    public string Move(string id, string direction)
    {
        lock (_lock)
        {
            var data = Load();
            var entry = data.Recipes.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (entry == null)
            {
                return BuildMutationJson(false, data, "custom recipe not found");
            }

            var ordered = data.Recipes
                .Where(item => item.CustomerId == entry.CustomerId)
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.CreatedAtUtc)
                .ToList();
            var index = ordered.FindIndex(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return BuildMutationJson(false, data, "custom recipe not found");
            }

            var offset = string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase)
                ? -1
                : string.Equals(direction, "down", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (offset == 0) return BuildMutationJson(false, data, "invalid custom recipe move direction");

            var targetIndex = index + offset;
            if (targetIndex < 0 || targetIndex >= ordered.Count) return BuildMutationJson(true, data, null);

            (ordered[index].SortOrder, ordered[targetIndex].SortOrder) = (ordered[targetIndex].SortOrder, ordered[index].SortOrder);
            ordered[index].UpdatedAtUtc = DateTime.UtcNow;
            ordered[targetIndex].UpdatedAtUtc = DateTime.UtcNow;

            NormalizeData(data);
            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    public int MigrateManualRecipeFavorites()
    {
        lock (_lock)
        {
            var migrated = _favoriteStore.ReadManualRecipeFavorites();
            if (migrated.Count == 0) return 0;

            var data = Load();
            var now = DateTime.UtcNow;
            var existingKeys = new HashSet<string>(
                data.Recipes.Select(entry => BuildRecipeKey(entry.CustomerId, entry.FoodTag, entry.FoodId, entry.ExtraIngredientIds)),
                StringComparer.Ordinal);
            var nextSortOrder = NextSortOrder(data);
            var addedCount = 0;

            foreach (var favorite in migrated)
            {
                var key = BuildRecipeKey(favorite.CustomerId, favorite.FoodTag, favorite.RecipeId, favorite.ExtraIngredientIds);
                if (!existingKeys.Add(key)) continue;

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
                addedCount++;
            }

            if (addedCount > 0)
            {
                NormalizeData(data);
                Save(data);
            }

            // Cross-file transaction order is intentional: persist the destination before deleting
            // legacy entries. A retry after interruption recognizes the destination keys and resumes here.
            _favoriteStore.RemoveManualRecipeFavorites();
            _log.LogInfo($"Migrated {migrated.Count} manual recipe favorites to custom-recipes.json ({addedCount} added).");
            return addedCount;
        }
    }

    private CustomRecipeData Load()
    {
        try
        {
            var data = JsonFileStore.LoadOrCreate<CustomRecipeData>(_path, JsonOptions);
            NormalizeData(data);
            return data;
        }
        catch (Exception ex)
        {
            _log.LogError($"Failed to load custom recipes from '{_path}': {ex.Message}");
            throw new InvalidDataException("The custom recipes file could not be read. The original file was not changed.", ex);
        }
    }

    private void Save(CustomRecipeData data)
    {
        data.Version = 1;
        JsonFileStore.Save(_path, data, JsonOptions);
    }

    private static void NormalizeData(CustomRecipeData data)
    {
        if (data.Version != 1)
        {
            throw new InvalidDataException($"Unsupported custom recipe schema version: {data.Version}.");
        }

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
        return JsonSerializer.Serialize(new LocalApiCustomRecipeMutationDto
        {
            Ok = ok,
            CustomRecipes = data,
            Error = string.IsNullOrWhiteSpace(error) ? null : error,
        }, JsonOptions);
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

    private static List<CustomRecipeEntry> SelectEntries(
        CustomRecipeData data,
        CustomRecipeSelection selection)
    {
        return selection.Kind switch
        {
            CustomRecipeSelectionKind.All => data.Recipes.ToList(),
            CustomRecipeSelectionKind.Customer => data.Recipes
                .Where(entry => entry.CustomerId == selection.CustomerId)
                .ToList(),
            CustomRecipeSelectionKind.Recipe => data.Recipes
                .Where(entry => entry.FoodId == selection.FoodId)
                .ToList(),
            CustomRecipeSelectionKind.Entry => data.Recipes
                .Where(entry => string.Equals(entry.Id, selection.Id, StringComparison.Ordinal))
                .ToList(),
            _ => new List<CustomRecipeEntry>(),
        };
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
    public bool? Enabled { get; init; }
    public bool? PinToTop { get; init; }
    public int? SortOrder { get; init; }
}

internal enum CustomRecipeSelectionKind
{
    All,
    Customer,
    Recipe,
    Entry,
}

internal sealed class CustomRecipeSelection
{
    public CustomRecipeSelectionKind Kind { get; init; }
    public string Id { get; init; } = "";
    public int CustomerId { get; init; } = -1;
    public int FoodId { get; init; } = -1;
}

internal sealed class CustomRecipeData
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public List<CustomRecipeEntry> Recipes { get; set; } = new();
}

internal sealed class LocalApiCustomRecipeMutationDto
{
    public bool Ok { get; init; }
    public CustomRecipeData CustomRecipes { get; init; } = new();
    public string? Error { get; init; }
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
