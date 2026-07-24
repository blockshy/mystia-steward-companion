using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeSchedulerCharacterIdentity
{
    private const int SpecialIdentity = 0;
    private const int NormalIdentity = 1;

    public static bool IsNormal(object boxedIdentity)
    {
        ArgumentNullException.ThrowIfNull(boxedIdentity);

        var type = boxedIdentity.GetType();
        if (!type.IsValueType)
        {
            throw new InvalidOperationException(
                $"{type.FullName} must be a boxed SchedulerNode.Character value.");
        }

        var matches = type
            .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(field => string.Equals(
                field.Name,
                "characterIdentity",
                StringComparison.Ordinal))
            .Take(2)
            .ToList();
        if (matches.Count != 1)
        {
            throw new MissingMemberException(type.FullName, "characterIdentity");
        }

        var field = matches[0];
        if (!field.FieldType.IsEnum)
        {
            throw new InvalidOperationException(
                $"{type.FullName}.characterIdentity must be an enum field.");
        }

        var value = field.GetValue(boxedIdentity);
        if (value == null || value.GetType() != field.FieldType)
        {
            throw new InvalidOperationException(
                $"{type.FullName}.characterIdentity returned an invalid enum value.");
        }

        return Convert.ToInt32(value) switch
        {
            SpecialIdentity => false,
            NormalIdentity => true,
            var unexpected => throw new InvalidOperationException(
                $"{type.FullName}.characterIdentity returned unsupported value {unexpected}."),
        };
    }
}
