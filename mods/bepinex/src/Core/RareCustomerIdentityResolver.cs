namespace MystiaStewardCompanion.Core;

public sealed record RareCustomerIdentity(int Id, string Name);

public sealed class RareCustomerIdentityResolver
{
    private static readonly IReadOnlyDictionary<int, string> RuntimeIdAliases = new Dictionary<int, string>
    {
        [16] = "因幡帝",
        [22] = "蕾米莉亚",
    };

    private static readonly IReadOnlyDictionary<string, string> RuntimeNameAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Tewi_HardSell"] = "因幡帝",
        ["Remilia"] = "蕾米莉亚",
    };

    private readonly IReadOnlyDictionary<int, RareCustomer> _customersById;
    private readonly IReadOnlyDictionary<string, RareCustomer> _customersByName;

    public RareCustomerIdentityResolver(
        IReadOnlyDictionary<int, RareCustomer> customersById,
        IEnumerable<RareCustomer> customers)
    {
        _customersById = customersById;
        _customersByName = customers
            .GroupBy(customer => customer.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public RareCustomerIdentity? Resolve(int? runtimeId, string? runtimeNameOrStringId)
    {
        if (runtimeId.HasValue && _customersById.TryGetValue(runtimeId.Value, out var directCustomer))
        {
            return new RareCustomerIdentity(directCustomer.Id, directCustomer.Name);
        }

        if (TryResolveByName(runtimeNameOrStringId, out var namedCustomer))
        {
            return new RareCustomerIdentity(namedCustomer.Id, namedCustomer.Name);
        }

        if (runtimeId.HasValue
            && RuntimeIdAliases.TryGetValue(runtimeId.Value, out var targetName)
            && _customersByName.TryGetValue(targetName, out var aliasedCustomer))
        {
            return new RareCustomerIdentity(aliasedCustomer.Id, aliasedCustomer.Name);
        }

        return null;
    }

    private bool TryResolveByName(string? runtimeNameOrStringId, out RareCustomer customer)
    {
        customer = null!;
        if (string.IsNullOrWhiteSpace(runtimeNameOrStringId)) return false;

        var key = runtimeNameOrStringId.Trim();
        if (_customersByName.TryGetValue(key, out var directCustomer))
        {
            customer = directCustomer;
            return true;
        }

        if (RuntimeNameAliases.TryGetValue(key, out var targetName)
            && _customersByName.TryGetValue(targetName, out var aliasedCustomer))
        {
            customer = aliasedCustomer;
            return true;
        }

        return false;
    }
}
