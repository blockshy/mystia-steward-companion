using System.Text;
using System.Text.Json;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;

var root = Path.Combine(Path.GetTempPath(), $"mystia-local-api-storage-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var log = Logger.CreateLogSource("local-api-storage-smoke");

try
{
    VerifyCorruptFavoriteIsPreserved(root, log);
    VerifyNullableFavoriteExtras(root, log);
    VerifyMutationJsonEscaping(root, log);
    VerifyCustomRecipeReadDoesNotWrite(root, log);
    VerifyCustomRecipeCrudDoesNotRunMigration(root, log);
    VerifyCustomRecipeManagement(root, log);
    VerifyManualFavoriteMigration(root, log);
    VerifyInterruptedManualFavoriteMigrationRecovery(root, log);
    VerifyFailedManualFavoriteMigrationPreservesFiles(root, log);
    VerifyCorruptCustomRecipeIsPreserved(root, log);
    VerifyFutureSchemasArePreserved(root, log);
    Console.WriteLine("PASS: storage files were preserved, normalized and migrated without destructive rewrites or duplicate custom recipes.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}
finally
{
    Logger.Sources.Remove(log);
    Directory.Delete(root, recursive: true);
}

static void VerifyCorruptFavoriteIsPreserved(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "favorites-corrupt.json");
    const string corruptJson = "{ not valid json";
    File.WriteAllText(path, corruptJson, Encoding.UTF8);
    var store = new FavoriteStore(path, log);

    ExpectInvalidData(store.GetJson);
    ExpectInvalidData(() => store.AddRecipe(1, "guest", "tag", 2, Array.Empty<int>()));
    AssertEqual(corruptJson, File.ReadAllText(path, Encoding.UTF8), "A failed favorite read changed the source file.");
}

static void VerifyNullableFavoriteExtras(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "favorites-null-extras.json");
    File.WriteAllText(
        path,
        "{\"version\":1,\"recipes\":[{\"id\":\"r\",\"customerId\":1,\"customerName\":\"g\",\"foodTag\":\"t\",\"recipeId\":2,\"extraIngredientIds\":null}],\"beverages\":[]}",
        Encoding.UTF8);

    using var document = JsonDocument.Parse(new FavoriteStore(path, log).GetJson());
    var extras = document.RootElement.GetProperty("recipes")[0].GetProperty("extraIngredientIds");
    AssertEqual(0, extras.GetArrayLength(), "Null favorite extraIngredientIds was not normalized.");
}

static void VerifyMutationJsonEscaping(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "favorites-roundtrip.json");
    var response = new FavoriteStore(path, log).AddRecipe(3, "guest \"quoted\"\nline", "tag\\name", 4, new[] { 9, 9, -1 });
    using var document = JsonDocument.Parse(response);
    var rootElement = document.RootElement;
    AssertEqual(true, rootElement.GetProperty("ok").GetBoolean(), "Mutation response did not report success.");
    var recipe = rootElement.GetProperty("favorites").GetProperty("recipes")[0];
    AssertEqual("guest \"quoted\"\nline", recipe.GetProperty("customerName").GetString(), "Customer name did not round-trip through JSON.");
    AssertEqual("tag\\name", recipe.GetProperty("foodTag").GetString(), "Food tag did not round-trip through JSON.");
    AssertEqual(1, recipe.GetProperty("extraIngredientIds").GetArrayLength(), "Extra ingredient IDs were not normalized.");
}

static void VerifyCustomRecipeReadDoesNotWrite(string root, ManualLogSource log)
{
    var favoritePath = Path.Combine(root, "favorites-empty.json");
    var path = Path.Combine(root, "custom-read.json");
    const string original = "{\n  \"version\": 1,\n  \"recipes\": []\n}\n";
    File.WriteAllText(path, original, new UTF8Encoding(false));

    var favoriteStore = new FavoriteStore(favoritePath, log);
    _ = new CustomRecipeStore(path, favoriteStore, log).GetJson();
    AssertEqual(original, File.ReadAllText(path, Encoding.UTF8), "Reading custom recipes rewrote the file.");
}

static void VerifyManualFavoriteMigration(string root, ManualLogSource log)
{
    var favoritePath = Path.Combine(root, "favorites-manual-migration.json");
    var customPath = Path.Combine(root, "custom-manual-migration.json");
    File.WriteAllText(
        favoritePath,
        "{\"version\":1,\"recipes\":["
        + "{\"id\":\"manual\",\"customerId\":3,\"customerName\":\"guest\",\"foodTag\":\"tag\",\"recipeId\":7,\"extraIngredientIds\":[9,9],\"source\":\"manual\"},"
        + "{\"id\":\"favorite\",\"customerId\":3,\"customerName\":\"guest\",\"foodTag\":\"tag\",\"recipeId\":8,\"extraIngredientIds\":[]}]"
        + ",\"beverages\":[]}",
        Encoding.UTF8);

    var favoriteStore = new FavoriteStore(favoritePath, log);
    var customStore = new CustomRecipeStore(customPath, favoriteStore, log);
    AssertEqual(1, customStore.MigrateManualRecipeFavorites(), "The explicit migration did not add the manual favorite.");
    using (var customRecipes = JsonDocument.Parse(File.ReadAllText(customPath, Encoding.UTF8)))
    {
        var recipes = customRecipes.RootElement.GetProperty("recipes");
        AssertEqual(1, recipes.GetArrayLength(), "The manual favorite was not migrated exactly once.");
        AssertEqual(7, recipes[0].GetProperty("foodId").GetInt32(), "The migrated custom recipe used the wrong food ID.");
        AssertEqual(1, recipes[0].GetProperty("extraIngredientIds").GetArrayLength(), "The migrated extra ingredient IDs were not normalized.");
    }

    using (var favorites = JsonDocument.Parse(File.ReadAllText(favoritePath, Encoding.UTF8)))
    {
        var recipes = favorites.RootElement.GetProperty("recipes");
        AssertEqual(1, recipes.GetArrayLength(), "The migrated manual favorite still exists on disk.");
        AssertEqual("favorite", recipes[0].GetProperty("id").GetString(), "A normal favorite was removed during migration.");
    }

    var rebuiltFavoriteStore = new FavoriteStore(favoritePath, log);
    var rebuiltCustomStore = new CustomRecipeStore(customPath, rebuiltFavoriteStore, log);
    AssertEqual(0, rebuiltCustomStore.MigrateManualRecipeFavorites(), "Rebuilding the stores repeated an already completed migration.");
    using var rebuiltCustomRecipes = JsonDocument.Parse(File.ReadAllText(customPath, Encoding.UTF8));
    AssertEqual(1, rebuiltCustomRecipes.RootElement.GetProperty("recipes").GetArrayLength(), "A rebuilt store duplicated the migrated custom recipe.");
}

static void VerifyCustomRecipeCrudDoesNotRunMigration(string root, ManualLogSource log)
{
    var favoritePath = Path.Combine(root, "favorites-manual-no-implicit-migration.json");
    var customPath = Path.Combine(root, "custom-no-implicit-migration.json");
    File.WriteAllText(
        favoritePath,
        "{\"version\":1,\"recipes\":[{\"id\":\"manual\",\"customerId\":4,\"customerName\":\"guest\",\"foodTag\":\"tag\",\"recipeId\":9,\"extraIngredientIds\":[],\"source\":\"manual\"}],\"beverages\":[]}",
        Encoding.UTF8);

    var store = new CustomRecipeStore(customPath, new FavoriteStore(favoritePath, log), log);
    using (var initial = JsonDocument.Parse(store.GetJson()))
    {
        AssertEqual(0, initial.RootElement.GetProperty("recipes").GetArrayLength(), "GET implicitly migrated a manual favorite.");
    }

    var upsertResponse = store.Upsert(new CustomRecipeMutation
    {
        CustomerId = 8,
        CustomerName = "other",
        FoodId = 12,
        RecipeId = -1,
        RecipeName = "custom",
        Enabled = true,
        PinToTop = true,
    });
    using var upsertDocument = JsonDocument.Parse(upsertResponse);
    var id = upsertDocument.RootElement.GetProperty("customRecipes").GetProperty("recipes")[0].GetProperty("id").GetString() ?? "";
    _ = store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.Entry, Id = id },
        enabled: false,
        pinToTop: null);
    _ = store.Move(id, "up");
    _ = store.Remove(id);

    using var favorites = JsonDocument.Parse(File.ReadAllText(favoritePath, Encoding.UTF8));
    var manualRecipes = favorites.RootElement.GetProperty("recipes");
    AssertEqual(1, manualRecipes.GetArrayLength(), "A custom recipe CRUD operation removed the manual favorite.");
    AssertEqual("manual", manualRecipes[0].GetProperty("id").GetString(), "A custom recipe CRUD operation changed the manual favorite.");
}

static void VerifyCustomRecipeManagement(string root, ManualLogSource log)
{
    var favoritePath = Path.Combine(root, "favorites-for-custom-management.json");
    var customPath = Path.Combine(root, "custom-management.json");
    var store = new CustomRecipeStore(customPath, new FavoriteStore(favoritePath, log), log);

    using (var initial = JsonDocument.Parse(store.GetJson()))
    {
        AssertEqual(true, initial.RootElement.GetProperty("enabled").GetBoolean(), "Custom recipes were not enabled by default.");
    }
    AssertMutationOk(store.SetEnabled(false), "Disabling all custom recipes failed.");
    using (var disabled = JsonDocument.Parse(store.GetJson()))
    {
        AssertEqual(false, disabled.RootElement.GetProperty("enabled").GetBoolean(), "The global custom recipe setting was not persisted.");
    }

    var firstCustomerFirst = AddCustomRecipe(store, 1, 10, 100);
    var secondCustomer = AddCustomRecipe(store, 2, 10, 200);
    var firstCustomerSecond = AddCustomRecipe(store, 1, 20, 300);

    AssertMutationOk(store.Move(secondCustomer, "down"), "Moving a single-entry customer group failed.");
    AssertEqual(200, ReadCustomRecipe(store, secondCustomer).GetProperty("sortOrder").GetInt32(), "Move crossed a customer boundary.");
    AssertMutationOk(store.Move(firstCustomerFirst, "down"), "Moving within a customer group failed.");
    AssertEqual(300, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("sortOrder").GetInt32(), "The source recipe was not moved within its customer group.");
    AssertEqual(100, ReadCustomRecipe(store, firstCustomerSecond).GetProperty("sortOrder").GetInt32(), "The target recipe was not moved within its customer group.");
    AssertEqual(200, ReadCustomRecipe(store, secondCustomer).GetProperty("sortOrder").GetInt32(), "Moving another customer changed an unrelated sort order.");

    AssertMutationOk(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.Customer, CustomerId = 1 },
        enabled: false,
        pinToTop: null), "Customer bulk disable failed.");
    AssertEqual(false, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("enabled").GetBoolean(), "Customer bulk disable missed the first entry.");
    AssertEqual(false, ReadCustomRecipe(store, firstCustomerSecond).GetProperty("enabled").GetBoolean(), "Customer bulk disable missed the second entry.");
    AssertEqual(true, ReadCustomRecipe(store, secondCustomer).GetProperty("enabled").GetBoolean(), "Customer bulk disable changed another customer.");

    AssertMutationOk(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.Recipe, FoodId = 10 },
        enabled: null,
        pinToTop: false), "Recipe bulk unpin failed.");
    AssertEqual(false, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("pinToTop").GetBoolean(), "Recipe bulk unpin missed the first entry.");
    AssertEqual(false, ReadCustomRecipe(store, secondCustomer).GetProperty("pinToTop").GetBoolean(), "Recipe bulk unpin missed the second entry.");
    AssertEqual(true, ReadCustomRecipe(store, firstCustomerSecond).GetProperty("pinToTop").GetBoolean(), "Recipe bulk unpin changed another recipe.");

    AssertMutationOk(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.All },
        enabled: true,
        pinToTop: true), "Updating all custom recipe flags failed.");
    AssertEqual(true, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("enabled").GetBoolean(), "Update-all did not restore enabled state.");
    AssertEqual(true, ReadCustomRecipe(store, secondCustomer).GetProperty("pinToTop").GetBoolean(), "Update-all did not restore pin state.");

    var beforeInvalidMutation = File.ReadAllText(customPath, Encoding.UTF8);
    AssertMutationFailed(store.Move(firstCustomerFirst, "sideways"), "An invalid move direction unexpectedly succeeded.");
    AssertMutationFailed(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.Entry, Id = "missing" },
        enabled: false,
        pinToTop: null), "A missing entry update unexpectedly succeeded.");
    AssertMutationFailed(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.Entry, Id = firstCustomerFirst },
        enabled: null,
        pinToTop: null), "A flagless update unexpectedly succeeded.");
    AssertEqual(beforeInvalidMutation, File.ReadAllText(customPath, Encoding.UTF8), "A rejected bulk mutation changed the custom recipe file.");

    AssertMutationOk(store.SetEnabled(true), "Re-enabling all custom recipes failed.");
}

static string AddCustomRecipe(CustomRecipeStore store, int customerId, int foodId, int sortOrder)
{
    using var response = JsonDocument.Parse(store.Upsert(new CustomRecipeMutation
    {
        CustomerId = customerId,
        CustomerName = $"guest-{customerId}",
        FoodId = foodId,
        RecipeId = foodId + 1000,
        RecipeName = $"recipe-{foodId}",
        Enabled = true,
        PinToTop = true,
        SortOrder = sortOrder,
    }));
    AssertEqual(true, response.RootElement.GetProperty("ok").GetBoolean(), "Adding a custom recipe failed.");
    return response.RootElement
        .GetProperty("customRecipes")
        .GetProperty("recipes")
        .EnumerateArray()
        .Single(recipe => recipe.GetProperty("customerId").GetInt32() == customerId
            && recipe.GetProperty("foodId").GetInt32() == foodId)
        .GetProperty("id")
        .GetString() ?? throw new InvalidOperationException("The added custom recipe has no ID.");
}

static JsonElement ReadCustomRecipe(CustomRecipeStore store, string id)
{
    using var document = JsonDocument.Parse(store.GetJson());
    return document.RootElement
        .GetProperty("recipes")
        .EnumerateArray()
        .Single(recipe => string.Equals(recipe.GetProperty("id").GetString(), id, StringComparison.Ordinal))
        .Clone();
}

static void AssertMutationOk(string response, string message)
{
    using var document = JsonDocument.Parse(response);
    AssertEqual(true, document.RootElement.GetProperty("ok").GetBoolean(), message);
}

static void AssertMutationFailed(string response, string message)
{
    using var document = JsonDocument.Parse(response);
    AssertEqual(false, document.RootElement.GetProperty("ok").GetBoolean(), message);
}

static void VerifyInterruptedManualFavoriteMigrationRecovery(string root, ManualLogSource log)
{
    var favoritePath = Path.Combine(root, "favorites-manual-recovery.json");
    var customPath = Path.Combine(root, "custom-manual-recovery.json");
    File.WriteAllText(
        favoritePath,
        "{\"version\":1,\"recipes\":[{\"id\":\"manual\",\"customerId\":5,\"customerName\":\"guest\",\"foodTag\":\"tag\",\"recipeId\":13,\"extraIngredientIds\":[2],\"source\":\"manual\"}],\"beverages\":[]}",
        Encoding.UTF8);
    File.WriteAllText(
        customPath,
        "{\"version\":1,\"recipes\":[{\"id\":\"persisted\",\"customerId\":5,\"customerName\":\"guest\",\"foodTag\":\"tag\",\"foodId\":13,\"recipeId\":-1,\"recipeName\":\"\",\"extraIngredientIds\":[2],\"enabled\":true,\"pinToTop\":true,\"sortOrder\":100}]}",
        Encoding.UTF8);

    var store = new CustomRecipeStore(customPath, new FavoriteStore(favoritePath, log), log);
    AssertEqual(0, store.MigrateManualRecipeFavorites(), "Recovery duplicated a custom recipe that was already persisted.");
    using (var customRecipes = JsonDocument.Parse(File.ReadAllText(customPath, Encoding.UTF8)))
    {
        AssertEqual(1, customRecipes.RootElement.GetProperty("recipes").GetArrayLength(), "Recovery changed the already persisted custom recipe count.");
    }
    using (var favorites = JsonDocument.Parse(File.ReadAllText(favoritePath, Encoding.UTF8)))
    {
        AssertEqual(0, favorites.RootElement.GetProperty("recipes").GetArrayLength(), "Recovery did not remove the legacy source entry from disk.");
    }
}

static void VerifyFailedManualFavoriteMigrationPreservesFiles(string root, ManualLogSource log)
{
    var favoritePath = Path.Combine(root, "favorites-manual-failed-migration.json");
    var customPath = Path.Combine(root, "custom-failed-migration.json");
    const string favorites = "{\"version\":1,\"recipes\":[{\"id\":\"manual\",\"customerId\":6,\"customerName\":\"guest\",\"foodTag\":\"tag\",\"recipeId\":14,\"extraIngredientIds\":[],\"source\":\"manual\"}],\"beverages\":[]}";
    const string corruptCustom = "{ not valid custom recipes";
    File.WriteAllText(favoritePath, favorites, Encoding.UTF8);
    File.WriteAllText(customPath, corruptCustom, Encoding.UTF8);

    var store = new CustomRecipeStore(customPath, new FavoriteStore(favoritePath, log), log);
    ExpectInvalidData(store.MigrateManualRecipeFavorites);
    AssertEqual(favorites, File.ReadAllText(favoritePath, Encoding.UTF8), "A failed migration changed the favorites source file.");
    AssertEqual(corruptCustom, File.ReadAllText(customPath, Encoding.UTF8), "A failed migration changed the custom recipe file.");
}

static void VerifyCorruptCustomRecipeIsPreserved(string root, ManualLogSource log)
{
    var favoritePath = Path.Combine(root, "favorites-for-custom.json");
    var path = Path.Combine(root, "custom-corrupt.json");
    const string corruptJson = "[ not an object";
    File.WriteAllText(path, corruptJson, Encoding.UTF8);
    var store = new CustomRecipeStore(path, new FavoriteStore(favoritePath, log), log);

    ExpectInvalidData(store.GetJson);
    AssertEqual(corruptJson, File.ReadAllText(path, Encoding.UTF8), "A failed custom recipe read changed the source file.");
}

static void VerifyFutureSchemasArePreserved(string root, ManualLogSource log)
{
    var favoritePath = Path.Combine(root, "favorites-future.json");
    const string futureFavorites = "{\"version\":2,\"recipes\":[],\"beverages\":[],\"futureField\":true}";
    File.WriteAllText(favoritePath, futureFavorites, Encoding.UTF8);
    ExpectInvalidData(new FavoriteStore(favoritePath, log).GetJson);
    AssertEqual(futureFavorites, File.ReadAllText(favoritePath, Encoding.UTF8), "A future favorites schema was rewritten.");

    var invalidFavoritePath = Path.Combine(root, "favorites-invalid-version.json");
    const string invalidFavorites = "{\"version\":0,\"recipes\":[],\"beverages\":[]}";
    File.WriteAllText(invalidFavoritePath, invalidFavorites, Encoding.UTF8);
    ExpectInvalidData(new FavoriteStore(invalidFavoritePath, log).GetJson);
    AssertEqual(invalidFavorites, File.ReadAllText(invalidFavoritePath, Encoding.UTF8), "An invalid favorites schema was rewritten.");

    var emptyFavoritePath = Path.Combine(root, "favorites-for-future-custom.json");
    var customPath = Path.Combine(root, "custom-future.json");
    const string futureCustom = "{\"version\":2,\"recipes\":[],\"futureField\":true}";
    File.WriteAllText(customPath, futureCustom, Encoding.UTF8);
    var customStore = new CustomRecipeStore(customPath, new FavoriteStore(emptyFavoritePath, log), log);
    ExpectInvalidData(customStore.GetJson);
    AssertEqual(futureCustom, File.ReadAllText(customPath, Encoding.UTF8), "A future custom recipe schema was rewritten.");
}

static void ExpectInvalidData<T>(Func<T> action)
{
    try
    {
        _ = action();
        throw new InvalidOperationException("Expected InvalidDataException was not thrown.");
    }
    catch (InvalidDataException)
    {
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}
