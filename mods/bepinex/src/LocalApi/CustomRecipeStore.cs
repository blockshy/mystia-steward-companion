using System.Text.Json;
using System.Text;
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
    private readonly ManualLogSource _log;

    public CustomRecipeStore(string path, ManualLogSource log)
    {
        _path = path;
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

    /// <summary>
    /// 设置总开关或切换当前模式。修订只在模式确实变化时增加，旧弹窗即使经历
    /// customer→food→customer，也不能用原修订写入；开关与模式仍是独立设置。
    /// </summary>
    public string SetSettings(bool? enabled, string? activeMode, long expectedModeRevision)
    {
        lock (_lock)
        {
            var data = Load();
            if (data.ModeRevision != expectedModeRevision)
                return BuildMutationJson(false, data, "custom recipe mode changed; reload before editing");
            if (enabled == null && activeMode == null || activeMode != null && !CustomRecipeModes.IsValid(activeMode))
                return BuildMutationJson(false, data, "invalid custom recipe settings");
            var changed = false;
            if (activeMode != null && data.ActiveMode != activeMode)
            {
                data.ModeRevision = checked(data.ModeRevision + 1);
                data.ActiveMode = activeMode;
                changed = true;
            }
            if (enabled != null && data.Enabled != enabled.Value)
            {
                data.Enabled = enabled.Value;
                changed = true;
            }
            if (changed) Save(data);

            return BuildMutationJson(true, data, null);
        }
    }

    public string Upsert(CustomRecipeMutation mutation)
    {
        lock (_lock)
        {
            var data = Load();
            if (!MatchesMode(data, mutation.Mode, mutation.ExpectedModeRevision))
                return BuildMutationJson(false, data, "custom recipe mode changed; reload before editing");
            if (mutation.CustomerId < 0 || mutation.FoodId < 0 || mutation.RecipeId < 0
                || string.IsNullOrWhiteSpace(mutation.CustomerName) || string.IsNullOrWhiteSpace(mutation.RecipeName)
                || mutation.SortOrder is <= 0 || mutation.ExtraIngredientIds.Any(id => id < 0))
                return BuildMutationJson(false, data, "invalid custom recipe parameters");
            var now = DateTime.UtcNow;
            var normalizedExtras = NormalizeIds(mutation.ExtraIngredientIds);
            var existing = string.IsNullOrWhiteSpace(mutation.Id)
                ? null
                : data.Recipes.FirstOrDefault(entry => string.Equals(entry.Id, mutation.Id, StringComparison.Ordinal));
            // 显式更新只允许已有且属于本模式的精确条目，不能因 ID 未命中而新增，
            // 更不能将另一模式的配方“搬入”当前模式。两模式都保留明确的稀客绑定。
            if (!string.IsNullOrWhiteSpace(mutation.Id) && (existing == null || existing.Mode != mutation.Mode))
                return BuildMutationJson(false, data, "custom recipe not found in current mode");

            if (existing == null)
            {
                data.Recipes.Add(new CustomRecipeEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Mode = mutation.Mode,
                    CustomerId = mutation.CustomerId,
                    CustomerName = mutation.CustomerName.Trim(),
                    FoodTag = NormalizeOptionalTag(mutation.FoodTag),
                    FoodId = mutation.FoodId,
                    RecipeId = mutation.RecipeId,
                    RecipeName = mutation.RecipeName.Trim(),
                    ExtraIngredientIds = normalizedExtras,
                    Enabled = mutation.Enabled ?? true,
                    PinToTop = mutation.PinToTop ?? true,
                    SortOrder = mutation.SortOrder ?? NextSortOrder(data, mutation.Mode, mutation.CustomerId, mutation.FoodId),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
            }
            else
            {
                var changedGroup = GroupKey(existing) != GroupKey(mutation.Mode, mutation.CustomerId, mutation.FoodId);
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
                else if (changedGroup) existing.SortOrder = NextSortOrder(data, mutation.Mode, mutation.CustomerId, mutation.FoodId);
                existing.UpdatedAtUtc = now;
            }

            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    public string Remove(string mode, long expectedModeRevision, string id)
    {
        lock (_lock)
        {
            var data = Load();
            if (!MatchesMode(data, mode, expectedModeRevision))
                return BuildMutationJson(false, data, "custom recipe mode changed; reload before editing");
            var entry = data.Recipes.SingleOrDefault(item => item.Id == id && item.Mode == mode);
            if (entry == null) return BuildMutationJson(false, data, "custom recipe not found in current mode");
            data.Recipes.Remove(entry);
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
            if (!MatchesMode(data, selection.Mode, selection.ExpectedModeRevision))
                return BuildMutationJson(false, data, "custom recipe mode changed; reload before editing");
            if (selection.Kind == CustomRecipeSelectionKind.Customer && selection.Mode != CustomRecipeModes.Customer)
                return BuildMutationJson(false, data, "customer selection requires customer mode");
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
                Save(data);
            }
            return BuildMutationJson(true, data, null);
        }
    }

    public string Move(string mode, long expectedModeRevision, string id, string direction)
    {
        lock (_lock)
        {
            var data = Load();
            if (!MatchesMode(data, mode, expectedModeRevision))
                return BuildMutationJson(false, data, "custom recipe mode changed; reload before editing");
            var entry = data.Recipes.FirstOrDefault(item => item.Mode == mode && string.Equals(item.Id, id, StringComparison.Ordinal));
            if (entry == null)
            {
                return BuildMutationJson(false, data, "custom recipe not found");
            }

            var ordered = data.Recipes
                .Where(item => item.Mode == mode && GroupKey(item) == GroupKey(entry))
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
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

            if (ordered[index].SortOrder != ordered[targetIndex].SortOrder)
                (ordered[index].SortOrder, ordered[targetIndex].SortOrder) = (ordered[targetIndex].SortOrder, ordered[index].SortOrder);
            else
            {
                // 历史条目可能拥有相同序号。相同数值不能靠交换完成移动；此时仅
                // 重编号当前组，其他客人／料理和另一模式的顺序及数值均保持不变。
                (ordered[index], ordered[targetIndex]) = (ordered[targetIndex], ordered[index]);
                for (var position = 0; position < ordered.Count; position++)
                    ordered[position].SortOrder = checked((position + 1) * 100);
            }
            ordered[index].UpdatedAtUtc = DateTime.UtcNow;
            ordered[targetIndex].UpdatedAtUtc = DateTime.UtcNow;

            Save(data);
            return BuildMutationJson(true, data, null);
        }
    }

    private CustomRecipeData Load()
    {
        try
        {
            if (!File.Exists(_path)) return new CustomRecipeData();
            // 原始 JSON 先校验再反序列化，不能让 POCO 默认值吞掉未知版本、缺失模式
            // 或重复字段。验证失败直接保留原文件，不回退成空配置覆盖用户绑定。
            var originalBytes = File.ReadAllBytes(_path);
            using var source = new StreamReader(new MemoryStream(originalBytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var document = JsonDocument.Parse(source.ReadToEnd());
            var version = ValidateDocument(document.RootElement);
            var data = document.RootElement.Deserialize<CustomRecipeData>(JsonOptions)
                ?? throw new InvalidDataException("Custom recipe document is null.");
            if (version == 1)
            {
                // 一次性磁盘迁移只添加模式元数据；已有 ID、绑定、标记、时间与顺序
                // 原样保留。迁移完成后重复读取不再写盘，HTTP 始终只输出 version 2。
                data.Version = 2;
                data.ActiveMode = CustomRecipeModes.Customer;
                data.ModeRevision = 0;
                foreach (var entry in data.Recipes) entry.Mode = CustomRecipeModes.Customer;
                ValidateData(data);
                PreserveVersionOneBackup(originalBytes);
                Save(data);
            }
            else ValidateData(data);
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
        ValidateData(data);
        JsonFileStore.Save(_path, data, JsonOptions);
    }

    /// <summary>
    /// 在替换旧版本之前保存原字节。临时文件完整落盘后才以不覆盖方式发布备份；
    /// 备份失败不写源文件。若上次只完成备份便中断，仅相同字节允许继续迁移，
    /// 不覆盖已有历史副本，也不把另一份配置的备份误当作本次恢复依据。
    /// </summary>
    private void PreserveVersionOneBackup(byte[] originalBytes)
    {
        var backupPath = _path + ".v1.backup";
        if (File.Exists(backupPath))
        {
            if (!File.ReadAllBytes(backupPath).SequenceEqual(originalBytes))
                throw new InvalidDataException("Existing custom recipe v1 backup does not match the source.");
            return;
        }
        var temporaryPath = backupPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(originalBytes, 0, originalBytes.Length);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, backupPath, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { /* 清理失败不能覆盖原始备份异常；源文件尚未开始替换。 */ }
        }
    }

    private static void ValidateData(CustomRecipeData data)
    {
        if (data.Version != 2 || !CustomRecipeModes.IsValid(data.ActiveMode) || data.ModeRevision < 0)
            throw new InvalidDataException($"Unsupported custom recipe schema version: {data.Version}.");
        if (data.Recipes == null || data.Recipes.Any(entry => entry == null
            || string.IsNullOrWhiteSpace(entry.Id) || !CustomRecipeModes.IsValid(entry.Mode)
            || entry.CustomerName == null || entry.RecipeName == null || entry.ExtraIngredientIds == null
            || entry.CustomerId < 0 || entry.FoodId < 0 || entry.RecipeId < 0 || entry.SortOrder < 0
            || entry.ExtraIngredientIds.Any(id => id < 0))
            || data.Recipes.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != data.Recipes.Count)
            throw new InvalidDataException("Invalid or ambiguous custom recipe entries.");
    }

    private static int NextSortOrder(CustomRecipeData data, string mode, int customerId, int foodId)
    {
        var entries = data.Recipes.Where(entry => entry.Mode == mode
            && GroupKey(entry) == GroupKey(mode, customerId, foodId)).ToArray();
        return entries.Length == 0 ? 100 : checked(Math.Max(0, entries.Max(entry => entry.SortOrder)) + 100);
    }

    private static int GroupKey(CustomRecipeEntry entry) => GroupKey(entry.Mode, entry.CustomerId, entry.FoodId);
    private static int GroupKey(string mode, int customerId, int foodId) => mode == CustomRecipeModes.Customer ? customerId : foodId;
    private static bool MatchesMode(CustomRecipeData data, string mode, long revision) =>
        CustomRecipeModes.IsValid(mode) && data.ActiveMode == mode && revision >= 0 && data.ModeRevision == revision;

    /// <summary>仅旧版已知的 enabled 缺省值允许迁移；其他结构损坏不自动修补。</summary>
    private static int ValidateDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var versionNode)
            || !versionNode.TryGetInt32(out var version) || version is not (1 or 2))
            throw new InvalidDataException("Unsupported custom recipe schema version.");
        ValidateProperties(root, version == 1
            ? new[] { "version", "enabled", "recipes" }
            : new[] { "version", "enabled", "activeMode", "modeRevision", "recipes" },
            version == 1 ? new[] { "enabled" } : Array.Empty<string>());
        if (root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Custom recipe enabled must be boolean.");
        if (version == 2 && (!CustomRecipeModes.IsValid(root.GetProperty("activeMode").GetString())
            || !root.GetProperty("modeRevision").TryGetInt64(out var revision) || revision < 0))
            throw new InvalidDataException("Invalid custom recipe mode boundary.");
        var recipes = root.GetProperty("recipes");
        if (recipes.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Custom recipes must be an array.");
        var fields = new[] { "id", "customerId", "customerName", "foodTag", "foodId", "recipeId", "recipeName",
            "extraIngredientIds", "enabled", "pinToTop", "sortOrder", "createdAtUtc", "updatedAtUtc" };
        foreach (var entry in recipes.EnumerateArray())
        {
            ValidateProperties(entry, version == 1 ? fields : fields.Append("mode").ToArray(), Array.Empty<string>());
            foreach (var field in new[] { "id", "customerName", "recipeName", "createdAtUtc", "updatedAtUtc" })
                if (entry.GetProperty(field).ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"Custom recipe {field} must be a string.");
            if (entry.GetProperty("foodTag").ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw new InvalidDataException("Custom recipe foodTag must be a string or null.");
            // Web 序列化选项允许读取数值字符串；磁盘契约仍只接受实际 JSON 整数。
            // 不让负 ID、错误数组或布尔值经默认/过滤变成另一份可执行配方。
            foreach (var field in new[] { "customerId", "foodId", "recipeId", "sortOrder" })
                if (entry.GetProperty(field).ValueKind != JsonValueKind.Number
                    || !entry.GetProperty(field).TryGetInt32(out var number) || number < 0)
                    throw new InvalidDataException($"Custom recipe {field} must be a nonnegative integer.");
            foreach (var field in new[] { "enabled", "pinToTop" })
                if (entry.GetProperty(field).ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException($"Custom recipe {field} must be boolean.");
            var extras = entry.GetProperty("extraIngredientIds");
            if (extras.ValueKind != JsonValueKind.Array || extras.EnumerateArray().Any(value =>
                value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id < 0))
                throw new InvalidDataException("Custom recipe extraIngredientIds must contain nonnegative integers.");
            if (version == 2 && !CustomRecipeModes.IsValid(entry.GetProperty("mode").GetString()))
                throw new InvalidDataException("Invalid custom recipe entry mode.");
        }
        return version;
    }

    private static void ValidateProperties(JsonElement element, string[] allowed, string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Custom recipe entry must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate custom recipe property.");
        if (allowed.Except(optional, StringComparer.Ordinal).Any(name => !names.Contains(name)))
            throw new InvalidDataException("Custom recipe property is missing.");
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
        var entries = data.Recipes.Where(entry => entry.Mode == selection.Mode);
        return selection.Kind switch
        {
            CustomRecipeSelectionKind.All => entries.ToList(),
            CustomRecipeSelectionKind.Customer => entries
                .Where(entry => entry.CustomerId == selection.CustomerId)
                .ToList(),
            CustomRecipeSelectionKind.Recipe => entries
                .Where(entry => entry.FoodId == selection.FoodId)
                .ToList(),
            CustomRecipeSelectionKind.Entry => entries
                .Where(entry => string.Equals(entry.Id, selection.Id, StringComparison.Ordinal))
                .ToList(),
            _ => new List<CustomRecipeEntry>(),
        };
    }

}

internal sealed class CustomRecipeMutation
{
    public string Mode { get; init; } = "";
    public long ExpectedModeRevision { get; init; } = -1;
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
    public string Mode { get; init; } = "";
    public long ExpectedModeRevision { get; init; } = -1;
    public CustomRecipeSelectionKind Kind { get; init; }
    public string Id { get; init; } = "";
    public int CustomerId { get; init; } = -1;
    public int FoodId { get; init; } = -1;
}

internal sealed class CustomRecipeData
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public string ActiveMode { get; set; } = CustomRecipeModes.Customer;
    public long ModeRevision { get; set; }
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
    public string Mode { get; set; } = "";
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

/// <summary>模式只决定配置集合与分组，任何模式的条目仍绑定精确 customerId。</summary>
internal static class CustomRecipeModes
{
    public const string Customer = "customer";
    public const string Food = "food";
    public static bool IsValid(string? value) => value is Customer or Food;
}
