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
        // 索引与本次目录实例同寿命。运行时目录更新会通过 FromRuntime 创建新的仓库，
        // 因此无需每次订单快照重新分组，也不会把旧目录的名称或喜好带入新目录。
        RecipesById = recipes.GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        BeveragesById = beverages.GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        NormalCustomersById = normalCustomers.GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        // 图片身份必须来自唯一目录项，不能继承业务索引重复 ID 取首项的宽松规则。
        // 订单采集服务会周期性重建，因此这两张纯托管索引属于仓库快照，并在目录替换时
        // 一并重建；避免每轮订单读取重复 GroupBy，也不会跨目录沿用旧名称或图片身份。
        NormalGuestImageNamesById = normalCustomers.GroupBy(item => item.Id)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Name);
        RareGuestImageNamesById = rareCustomers.GroupBy(item => item.Id)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Name);
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
    public IReadOnlyDictionary<int, Recipe> RecipesById { get; }
    public IReadOnlyDictionary<int, Beverage> BeveragesById { get; }
    public IReadOnlyDictionary<int, NormalCustomer> NormalCustomersById { get; }
    /// <summary>仅含唯一 ID 的普客客群名称，供精确类型核验后的只读图片身份使用。</summary>
    public IReadOnlyDictionary<int, string> NormalGuestImageNamesById { get; }
    /// <summary>仅含唯一 ID 的稀客名称；命名空间与同数字普客客群独立。</summary>
    public IReadOnlyDictionary<int, string> RareGuestImageNamesById { get; }

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
