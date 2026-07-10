namespace MystiaStewardCompanion.Core;

public sealed class DataRepository
{
    private DataRepository(
        List<Recipe> recipes,
        List<Ingredient> ingredients,
        List<Beverage> beverages,
        List<NormalCustomer> normalCustomers,
        List<RareCustomer> rareCustomers,
        Dictionary<string, string> foodTagIdMap,
        Dictionary<string, string> beverageTagIdMap)
    {
        Recipes = recipes;
        Ingredients = ingredients;
        Beverages = beverages;
        NormalCustomers = normalCustomers;
        RareCustomers = rareCustomers;
        FoodTagIdMap = foodTagIdMap;
        BeverageTagIdMap = beverageTagIdMap;
        RecipeIdToId = recipes
            .GroupBy(r => r.RecipeId)
            .ToDictionary(group => group.Key, group => group.First().Id);
        RareCustomersById = rareCustomers
            .GroupBy(c => c.Id)
            .ToDictionary(group => group.Key, group => group.First());
        RareCustomerIdentities = new RareCustomerIdentityResolver(RareCustomersById, rareCustomers);
    }

    public IReadOnlyList<Recipe> Recipes { get; }
    public IReadOnlyList<Ingredient> Ingredients { get; }
    public IReadOnlyList<Beverage> Beverages { get; }
    public IReadOnlyList<NormalCustomer> NormalCustomers { get; }
    public IReadOnlyList<RareCustomer> RareCustomers { get; }
    public IReadOnlyDictionary<string, string> FoodTagIdMap { get; }
    public IReadOnlyDictionary<string, string> BeverageTagIdMap { get; }
    public IReadOnlyDictionary<int, int> RecipeIdToId { get; }
    public IReadOnlyDictionary<int, RareCustomer> RareCustomersById { get; }
    public RareCustomerIdentityResolver RareCustomerIdentities { get; }

    public static DataRepository FromRuntime(RuntimeDataCatalog catalog)
    {
        return new DataRepository(
            catalog.Recipes.ToList(),
            catalog.Ingredients.ToList(),
            catalog.Beverages.ToList(),
            catalog.NormalCustomers.ToList(),
            catalog.RareCustomers.ToList(),
            new Dictionary<string, string>(catalog.FoodTagIdMap, StringComparer.Ordinal),
            new Dictionary<string, string>(catalog.BeverageTagIdMap, StringComparer.Ordinal));
    }

    public static DataRepository Empty()
    {
        return new DataRepository(
            new List<Recipe>(),
            new List<Ingredient>(),
            new List<Beverage>(),
            new List<NormalCustomer>(),
            new List<RareCustomer>(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

}
