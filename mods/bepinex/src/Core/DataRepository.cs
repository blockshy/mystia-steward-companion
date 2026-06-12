namespace MystiaStewardCompanion.Core;

public sealed class DataRepository
{
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
        IngredientsByName = ingredients
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .GroupBy(i => i.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        IngredientsById = ingredients
            .GroupBy(i => i.Id)
            .ToDictionary(group => group.Key, group => group.First());
        RecipeIdToId = recipes
            .GroupBy(r => r.RecipeId)
            .ToDictionary(group => group.Key, group => group.First().Id);
        RareCustomersById = rareCustomers
            .GroupBy(c => c.Id)
            .ToDictionary(group => group.Key, group => group.First());
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

    public static DataRepository FromRuntime(RuntimeDataCatalog catalog, string dataDirectory)
    {
        return new DataRepository(
            string.IsNullOrWhiteSpace(dataDirectory) ? "runtime" : dataDirectory,
            catalog.Recipes.ToList(),
            catalog.Ingredients.ToList(),
            catalog.Beverages.ToList(),
            catalog.NormalCustomers.ToList(),
            catalog.RareCustomers.ToList(),
            new Dictionary<string, string>(catalog.FoodTagIdMap, StringComparer.Ordinal));
    }

    public static DataRepository Empty(string dataDirectory)
    {
        return new DataRepository(
            dataDirectory,
            new List<Recipe>(),
            new List<Ingredient>(),
            new List<Beverage>(),
            new List<NormalCustomer>(),
            new List<RareCustomer>(),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public IReadOnlyList<NormalCustomer> GetNormalCustomersByPlace(string place)
    {
        return NormalCustomers.Where(c => c.Places.Contains(place)).ToList();
    }

    public IReadOnlyList<RareCustomer> GetRareCustomersByPlace(string place)
    {
        return RareCustomers.Where(c => c.Places.Contains(place)).ToList();
    }

}
