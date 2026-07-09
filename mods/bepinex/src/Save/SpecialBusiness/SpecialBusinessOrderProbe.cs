namespace MystiaStewardCompanion.Save;

internal sealed record SpecialBusinessOrderProbe(int? Id, string Text)
{
    public static SpecialBusinessOrderProbe Read(object? order, object? controller)
    {
        var guest = RuntimeReflectionUtility.GetMemberValue(order, "SpecialGuests")
            ?? RuntimeReflectionUtility.GetMemberValue(order, "SpecialGuest")
            ?? RuntimeReflectionUtility.GetMemberValue(order, "Guest")
            ?? RuntimeReflectionUtility.InvokeMethod(order, "get_Guest")
            ?? RuntimeReflectionUtility.GetMemberValue(controller, "SpecialGuest")
            ?? RuntimeReflectionUtility.GetMemberValue(controller, "OrderingGuest");
        guest = RuntimeReflectionUtility.NormalizeKeyValueValue(guest) ?? guest;

        var id = ReadGuestId(guest);
        var textParts = new[]
        {
            CleanText(ReadFirstMember(guest, "Name", "name", "DisplayName", "displayName", "StringId", "stringId", "Id", "id")),
            CleanText(guest),
            CleanText(controller?.GetType().FullName),
            CleanText(order?.GetType().FullName),
        };
        return new SpecialBusinessOrderProbe(id, string.Join(" ", textParts.Where(part => part.Length > 0)));
    }

    public bool IsGuest(int id, params string[] aliases)
    {
        if (Id == id) return true;
        return ContainsAny(Text, aliases);
    }

    public bool IsGuest(IReadOnlySet<int> ids, params string[] aliases)
    {
        if (Id.HasValue && ids.Contains(Id.Value)) return true;
        return ContainsAny(Text, aliases);
    }

    public static bool HasControllerSpawnType(object? controller, string expected)
    {
        var spawnType = ReadControllerSpawnType(controller);
        return spawnType.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string ReadControllerSpawnType(object? controller)
    {
        return CleanText(ReadFirstMember(
            controller,
            "GuestControllerSpawnType",
            "guestControllerSpawnType",
            "SpawnType",
            "spawnType"));
    }

    public static bool ReadControllerBool(object? controller, params string[] members)
    {
        foreach (var member in members)
        {
            var value = ReadFirstMember(controller, member);
            if (value is bool boolean) return boolean;

            var text = CleanText(value);
            if (bool.TryParse(text, out var parsed)) return parsed;
        }

        return false;
    }

    private static int? ReadGuestId(object? guest)
    {
        foreach (var member in new[] { "Id", "ID", "id", "CharacterID", "characterID", "SourceGuestId", "sourceGuestId" })
        {
            var parsed = RuntimeReflectionUtility.ToInt(
                RuntimeReflectionUtility.GetMemberValue(guest, member)
                ?? RuntimeReflectionUtility.InvokeMethod(guest, $"get_{member}"),
                int.MinValue);
            if (parsed != int.MinValue) return parsed;
        }

        return null;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return needles.Any(needle => value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static object? ReadFirstMember(object? value, params string[] members)
    {
        foreach (var member in members)
        {
            var result = RuntimeReflectionUtility.GetMemberValue(value, member)
                ?? RuntimeReflectionUtility.InvokeMethod(value, $"get_{member}");
            if (result != null) return result;
        }

        return null;
    }

    private static string CleanText(object? value)
    {
        if (value == null) return "";
        try
        {
            return value.ToString()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
