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
    VerifyFavoriteManagementMutations(root, log);
    VerifyCustomRecipeReadDoesNotWrite(root, log);
    VerifyCustomRecipeManagement(root, log);
    VerifyCorruptCustomRecipeIsPreserved(root, log);
    VerifyFutureSchemasArePreserved(root, log);
    VerifyCompanionDeviceAuthority(root, log);
    VerifyCorruptDeviceAuthorityIsPreserved(root, log);
    Console.WriteLine("PASS: local API file stores and companion device configuration authority passed storage, CAS and corruption checks.");
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

static void VerifyCompanionDeviceAuthority(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "companion-devices.json");
    var store = new CompanionDeviceAuthorityStore(path, log);
    var now = DateTime.UtcNow;
    var primaryProfile = BuildSharedProfile(automationEnabled: true, rareConcurrency: 2);
    var secondaryProfile = BuildSharedProfile(automationEnabled: false, rareConcurrency: 3);

    var primary = store.Register(
        "11111111-1111-1111-1111-111111111111",
        "Windows 主设备",
        RegisterRequest("windows", primaryProfile),
        now);
    AssertEqual(true, primary.CurrentDeviceIsPrimary, "The first registered device did not become primary.");
    AssertEqual(1L, primary.AuthorityRevision, "Initial authority revision is invalid.");
    AssertEqual(1, primary.Devices.Count, "Initial device registry count is invalid.");

    var secondary = store.Register(
        "22222222-2222-2222-2222-222222222222",
        "Android 设备",
        RegisterRequest("android", secondaryProfile),
        now.AddSeconds(1));
    AssertEqual(false, secondary.CurrentDeviceIsPrimary, "The second registered device unexpectedly became primary.");
    AssertEqual(
        true,
        secondary.ActiveProfile.GetProperty("automationEnabled").GetBoolean(),
        "A secondary device did not receive the primary active profile.");
    AssertEqual(
        false,
        secondary.CurrentDeviceProfile.GetProperty("automationEnabled").GetBoolean(),
        "A secondary device's own stored profile was overwritten during registration.");

    ExpectAuthorityError(
        403,
        () => store.UpdatePrimaryProfile(
            secondary.CurrentDeviceId,
            new CompanionDeviceProfileUpdateRequest
            {
                ProtocolVersion = 1,
                ProfileSchemaVersion = 1,
                ExpectedAuthorityRevision = secondary.AuthorityRevision,
                ExpectedProfileRevision = secondary.CurrentDeviceProfileRevision,
                Profile = secondaryProfile,
            },
            now.AddSeconds(2)));

    var updatedProfile = BuildSharedProfile(automationEnabled: true, rareConcurrency: 4);
    var updated = store.UpdatePrimaryProfile(
        primary.CurrentDeviceId,
        new CompanionDeviceProfileUpdateRequest
        {
            ProtocolVersion = 1,
            ProfileSchemaVersion = 1,
            ExpectedAuthorityRevision = secondary.AuthorityRevision,
            ExpectedProfileRevision = primary.CurrentDeviceProfileRevision,
            Profile = updatedProfile,
        },
        now.AddSeconds(3));
    AssertEqual(2L, updated.AuthorityRevision, "Changing the active profile did not advance authority.");
    AssertEqual(4, updated.ActiveProfile.GetProperty("autoRareConcurrency").GetInt32(), "The active profile update was not persisted.");
    ExpectAuthorityError(
        409,
        () => store.UpdatePrimaryProfile(
            primary.CurrentDeviceId,
            new CompanionDeviceProfileUpdateRequest
            {
                ProtocolVersion = 1,
                ProfileSchemaVersion = 1,
                ExpectedAuthorityRevision = secondary.AuthorityRevision,
                ExpectedProfileRevision = primary.CurrentDeviceProfileRevision,
                Profile = primaryProfile,
            },
            now.AddSeconds(4)));

    AssertEqual(
        true,
        store.TryAuthorizePrimary(primary.CurrentDeviceId, updated.AuthorityRevision, now.AddSeconds(4), out var primaryWriterError),
        $"The current primary was not authorized as runtime writer: {primaryWriterError}");
    AssertEqual(
        false,
        store.TryAuthorizePrimary(secondary.CurrentDeviceId, updated.AuthorityRevision, now.AddSeconds(4), out _),
        "A secondary device was authorized as runtime writer.");

    var synced = store.SyncFromPrimary(
        secondary.CurrentDeviceId,
        new CompanionDeviceSyncRequest
        {
            ProtocolVersion = 1,
            ExpectedAuthorityRevision = updated.AuthorityRevision,
            DeviceId = secondary.CurrentDeviceId,
        },
        now.AddSeconds(5));
    var syncedDevice = synced.Devices.Single(device => device.DeviceId == secondary.CurrentDeviceId);
    AssertEqual(true, syncedDevice.SyncPending, "One-way profile synchronization did not create a pending acknowledgement.");
    var secondaryPending = store.Read(secondary.CurrentDeviceId, now.AddSeconds(6));
    AssertEqual(true, !string.IsNullOrWhiteSpace(secondaryPending.PendingSyncId), "Pending sync ID was not exposed to its target device.");
    var acknowledged = store.AcknowledgeSync(
        secondary.CurrentDeviceId,
        new CompanionDeviceSyncAckRequest
        {
            ProtocolVersion = 1,
            SyncId = secondaryPending.PendingSyncId!,
            ProfileRevision = secondaryPending.CurrentDeviceProfileRevision,
            ProfileHash = secondaryPending.CurrentDeviceProfileHash,
        },
        now.AddSeconds(7));
    AssertEqual(null, acknowledged.PendingSyncId, "Acknowledged sync was not retired.");

    var switched = store.SetPrimary(
        primary.CurrentDeviceId,
        new CompanionDeviceSetPrimaryRequest
        {
            ProtocolVersion = 1,
            ExpectedAuthorityRevision = acknowledged.AuthorityRevision,
            DeviceId = secondary.CurrentDeviceId,
        },
        now.AddSeconds(8));
    AssertEqual(true, switched.Changed, "Primary transfer did not report a state change.");
    AssertEqual(secondary.CurrentDeviceId, switched.State.PrimaryDeviceId, "Primary transfer selected the wrong device.");
    AssertEqual(3L, switched.State.AuthorityRevision, "Primary transfer did not advance authority.");
    AssertEqual(
        switched.State.ActiveProfileHash,
        switched.State.CurrentDeviceProfileHash,
        "The new primary did not activate its exact stored profile.");
    AssertEqual(
        false,
        store.TryAuthorizePrimary(primary.CurrentDeviceId, updated.AuthorityRevision, now.AddSeconds(8), out _),
        "The former primary retained runtime-writer authority after transfer.");
    AssertEqual(
        true,
        store.TryAuthorizePrimary(secondary.CurrentDeviceId, switched.State.AuthorityRevision, now.AddSeconds(8), out var secondaryWriterError),
        $"The new primary did not receive runtime-writer authority: {secondaryWriterError}");

    var reloaded = new CompanionDeviceAuthorityStore(path, log).Register(
        secondary.CurrentDeviceId,
        "Ignored registration label",
        RegisterRequest("android", secondaryProfile),
        now.AddSeconds(9));
    AssertEqual(secondary.CurrentDeviceId, reloaded.PrimaryDeviceId, "Primary identity did not survive a store reload.");
    AssertEqual("Android 设备", reloaded.Devices.Single(device => device.IsCurrent).Label, "Registration unexpectedly renamed an existing device.");

    using var invalidDocument = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["automationEnabled"] = true,
    }));
    ExpectAuthorityError(
        400,
        () => new CompanionDeviceAuthorityStore(Path.Combine(root, "invalid-profile.json"), log).Register(
            "33333333-3333-3333-3333-333333333333",
            "Invalid",
            RegisterRequest("browser", invalidDocument.RootElement.Clone()),
            now));
}

static void VerifyCorruptDeviceAuthorityIsPreserved(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "companion-devices-corrupt.json");
    const string corrupt = "{ broken device registry";
    File.WriteAllText(path, corrupt, Encoding.UTF8);
    var store = new CompanionDeviceAuthorityStore(path, log);
    ExpectAuthorityError(
        503,
        () => store.Register(
            "44444444-4444-4444-4444-444444444444",
            "Device",
            RegisterRequest("windows", BuildSharedProfile(false, 2)),
            DateTime.UtcNow));
    AssertEqual(corrupt, File.ReadAllText(path, Encoding.UTF8), "A corrupt device registry was overwritten.");
}

static CompanionDeviceRegisterRequest RegisterRequest(string platform, JsonElement profile)
{
    return new CompanionDeviceRegisterRequest
    {
        ProtocolVersion = 1,
        ProfileSchemaVersion = 1,
        Platform = platform,
        AppVersion = "1.2.0",
        Profile = profile,
    };
}

static JsonElement BuildSharedProfile(bool automationEnabled, int rareConcurrency)
{
    var booleanFields = new[]
    {
        "automationEnabled", "autoRareOrderEnabled", "autoNormalOrderEnabled",
        "autoNormalTakeBeverage", "autoNormalStartCooking", "autoNormalDeliverFood",
        "autoNormalCompleteOrder", "autoNormalStopOnError", "autoPrepCompleteOrder",
        "autoPrepTakeBeverage", "autoPrepStartCooking", "autoPrepCollectCooking",
        "autoPrepRecipeFavoritesOnly", "autoPrepBeverageFavoritesOnly", "autoPrepStopOnError",
        "filterMissingCookers", "missionRecipePriorityEnabled", "pinFavoriteRecipeEnabled",
        "pinFavoriteBeverageEnabled", "rareGameUiPinningEnabled", "normalGameUiPinningEnabled",
        "rareRecipeVariantEnabled", "normalRecipeVariantEnabled", "rareCookerHighlightEnabled",
        "normalCookerHighlightEnabled", "rareSeatHighlightEnabled", "normalSeatHighlightEnabled",
        "rareOrderHighlightEnabled", "normalOrderHighlightEnabled",
    };
    var profile = booleanFields.ToDictionary(field => field, _ => (object)false, StringComparer.Ordinal);
    profile["automationEnabled"] = automationEnabled;
    profile["autoRareOrderEnabled"] = true;
    profile["filterMissingCookers"] = true;
    profile["missionRecipePriorityEnabled"] = true;
    profile["autoRareConcurrency"] = rareConcurrency;
    profile["autoNormalConcurrency"] = 3;
    profile["autoMaxStepRetries"] = 3;
    profile["autoMaxRollbacks"] = 2;
    profile["rareTargetHighlightColor"] = "#FFDB2E";
    profile["normalTargetHighlightColor"] = "#5FACD3";
    profile["serviceOrderSortMode"] = "ordered";
    profile["recommendationBudgetPolicy"] = "block";
    profile["recipeVariantLimitPerBase"] = 1;
    var objectiveKeys = new[]
    {
        "foodPreference", "beveragePreference", "negativeRisk", "extraCount", "resourcePressure",
        "totalCost", "profit", "beverageStock", "cookerAvailable",
    };
    profile["recommendationSortProfile"] = new Dictionary<string, object?>
    {
        ["preset"] = "balanced",
        ["objectives"] = objectiveKeys.Select(key => new Dictionary<string, object?>
        {
            ["key"] = key,
            ["enabled"] = true,
            ["weight"] = 50,
            ["direction"] = key is "negativeRisk" or "extraCount" or "resourcePressure" or "totalCost" ? "asc" : "desc",
        }).ToArray(),
    };
    profile["recommendationExclusions"] = new Dictionary<string, object?>
    {
        ["excludedIngredientIds"] = Array.Empty<int>(),
        ["excludedBeverageIds"] = Array.Empty<int>(),
    };
    return JsonSerializer.SerializeToElement(profile);
}

static void ExpectAuthorityError(int statusCode, Action action)
{
    try
    {
        action();
        throw new InvalidOperationException($"Expected device authority error {statusCode} was not thrown.");
    }
    catch (CompanionDeviceAuthorityException ex) when (ex.StatusCode == statusCode)
    {
    }
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

static void VerifyFavoriteManagementMutations(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "favorites-management.json");
    var store = new FavoriteStore(path, log);
    using var firstRecipeResponse = JsonDocument.Parse(store.AddRecipe(3, "guest", "sweet", 4, new[] { 9 }));
    var firstRecipeId = firstRecipeResponse.RootElement
        .GetProperty("favorites")
        .GetProperty("recipes")[0]
        .GetProperty("id")
        .GetString() ?? throw new InvalidOperationException("The first recipe favorite has no ID.");
    using var secondRecipeResponse = JsonDocument.Parse(store.AddRecipe(3, "guest", "fresh", 5, Array.Empty<int>()));
    var secondRecipeId = secondRecipeResponse.RootElement
        .GetProperty("favorites")
        .GetProperty("recipes")
        .EnumerateArray()
        .Single(entry => entry.GetProperty("recipeId").GetInt32() == 5)
        .GetProperty("id")
        .GetString() ?? throw new InvalidOperationException("The second recipe favorite has no ID.");
    using var beverageResponse = JsonDocument.Parse(store.AddBeverage(3, "guest", "fruit", 6));
    var beverageId = beverageResponse.RootElement
        .GetProperty("favorites")
        .GetProperty("beverages")[0]
        .GetProperty("id")
        .GetString() ?? throw new InvalidOperationException("The beverage favorite has no ID.");

    using (var removedRecipe = JsonDocument.Parse(store.RemoveRecipe(firstRecipeId)))
    {
        var favorites = removedRecipe.RootElement.GetProperty("favorites");
        var recipes = favorites.GetProperty("recipes");
        AssertEqual(1, recipes.GetArrayLength(), "Removing one recipe favorite changed the wrong number of recipes.");
        AssertEqual(secondRecipeId, recipes[0].GetProperty("id").GetString(), "Removing one recipe favorite changed an unrelated recipe.");
        AssertEqual(beverageId, favorites.GetProperty("beverages")[0].GetProperty("id").GetString(), "Removing a recipe favorite changed the beverage favorite.");
    }

    using (var removedBeverage = JsonDocument.Parse(store.RemoveBeverage(beverageId)))
    {
        var favorites = removedBeverage.RootElement.GetProperty("favorites");
        AssertEqual(0, favorites.GetProperty("beverages").GetArrayLength(), "Removing the beverage favorite did not remove its exact entry.");
        AssertEqual(secondRecipeId, favorites.GetProperty("recipes")[0].GetProperty("id").GetString(), "Removing a beverage favorite changed the recipe favorite.");
    }
}

static void VerifyCustomRecipeReadDoesNotWrite(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "custom-read.json");
    const string original = "{\n  \"version\": 1,\n  \"recipes\": []\n}\n";
    File.WriteAllText(path, original, new UTF8Encoding(false));

    _ = new CustomRecipeStore(path, log).GetJson();
    AssertEqual(original, File.ReadAllText(path, Encoding.UTF8), "Reading custom recipes rewrote the file.");
}

static void VerifyCustomRecipeManagement(string root, ManualLogSource log)
{
    var customPath = Path.Combine(root, "custom-management.json");
    var store = new CustomRecipeStore(customPath, log);

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

static void VerifyCorruptCustomRecipeIsPreserved(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "custom-corrupt.json");
    const string corruptJson = "[ not an object";
    File.WriteAllText(path, corruptJson, Encoding.UTF8);
    var store = new CustomRecipeStore(path, log);

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

    var customPath = Path.Combine(root, "custom-future.json");
    const string futureCustom = "{\"version\":2,\"recipes\":[],\"futureField\":true}";
    File.WriteAllText(customPath, futureCustom, Encoding.UTF8);
    var customStore = new CustomRecipeStore(customPath, log);
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
