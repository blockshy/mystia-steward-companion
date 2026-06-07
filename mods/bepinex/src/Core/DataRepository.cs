using System.Text.Json;

namespace MystiaStewardCompanion.Core;

public sealed class DataRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private DataRepository(
        string dataDirectory,
        List<Recipe> recipes,
        List<Ingredient> ingredients,
        List<Beverage> beverages,
        List<NormalCustomer> normalCustomers,
        List<RareCustomer> rareCustomers,
        Dictionary<string, string> foodTagIdMap)
    {
        DataDirectory = dataDirectory;
        Recipes = recipes;
        Ingredients = ingredients;
        Beverages = beverages;
        NormalCustomers = normalCustomers;
        RareCustomers = rareCustomers;
        FoodTagIdMap = foodTagIdMap;
        IngredientsByName = ingredients.ToDictionary(i => i.Name, i => i);
        IngredientsById = ingredients.ToDictionary(i => i.Id, i => i);
        RecipeIdToId = recipes.ToDictionary(r => r.RecipeId, r => r.Id);
        RareCustomersById = rareCustomers.ToDictionary(c => c.Id, c => c);
        RareCustomerIdentities = new RareCustomerIdentityResolver(RareCustomersById, rareCustomers);
    }

    public string DataDirectory { get; }
    public IReadOnlyList<Recipe> Recipes { get; }
    public IReadOnlyList<Ingredient> Ingredients { get; }
    public IReadOnlyList<Beverage> Beverages { get; }
    public IReadOnlyList<NormalCustomer> NormalCustomers { get; }
    public IReadOnlyList<RareCustomer> RareCustomers { get; }
    public IReadOnlyDictionary<string, string> FoodTagIdMap { get; }
    public IReadOnlyDictionary<string, Ingredient> IngredientsByName { get; }
    public IReadOnlyDictionary<int, Ingredient> IngredientsById { get; }
    public IReadOnlyDictionary<int, int> RecipeIdToId { get; }
    public IReadOnlyDictionary<int, RareCustomer> RareCustomersById { get; }
    public RareCustomerIdentityResolver RareCustomerIdentities { get; }

    public static DataRepository Load(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            throw new DirectoryNotFoundException($"Data directory not found: {dataDirectory}");
        }

        return new DataRepository(
            dataDirectory,
            LoadJson<List<Recipe>>(dataDirectory, "recipes.json"),
            LoadJson<List<Ingredient>>(dataDirectory, "ingredients.json"),
            LoadJson<List<Beverage>>(dataDirectory, "beverages.json"),
            LoadJson<List<NormalCustomer>>(dataDirectory, "customer_normal.json"),
            LoadJson<List<RareCustomer>>(dataDirectory, "customer_rare.json"),
            LoadJson<Dictionary<string, string>>(dataDirectory, "food-tag-id-map.json"));
    }

    public IReadOnlyList<NormalCustomer> GetNormalCustomersByPlace(string place)
    {
        return NormalCustomers.Where(c => c.Places.Contains(place)).ToList();
    }

    public IReadOnlyList<RareCustomer> GetRareCustomersByPlace(string place)
    {
        return RareCustomers.Where(c => c.Places.Contains(place)).ToList();
    }

    private static T LoadJson<T>(string dataDirectory, string fileName)
    {
        var path = Path.Combine(dataDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required data file not found: {path}", path);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidDataException($"Failed to parse {path}");
    }
}
