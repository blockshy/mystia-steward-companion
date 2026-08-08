namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private static bool TryValidateServedFoodExtraIngredients(
        IReadOnlyList<int> expectedExtraIngredientIds,
        object servedFood,
        out IReadOnlyList<int> actualExtraIngredientIds,
        out string diagnostic)
    {
        actualExtraIngredientIds = Array.Empty<int>();
        if (!TryReadExactMemberValue(
                servedFood,
                out var rawModifier,
                out var modifierReadDiagnostic,
                "Modifier")
            || rawModifier == null)
        {
            diagnostic = $"actual extra ingredients unreadable; member={modifierReadDiagnostic}";
            return false;
        }

        if (!RuntimeConcreteCollectionReader.TryReadIntArray(
                rawModifier,
                out var rawActualExtraIngredientIds,
                out var modifierArrayFailure))
        {
            diagnostic = $"actual extra ingredients array unreadable: {modifierArrayFailure}";
            return false;
        }

        if (rawActualExtraIngredientIds.Any(id => id < 0)
            || rawActualExtraIngredientIds.Distinct().Count() != rawActualExtraIngredientIds.Count)
        {
            diagnostic =
                "actual extra ingredients contain invalid or duplicate ids: "
                + $"{SpecialBusinessDiagnostics.FormatIds(rawActualExtraIngredientIds)}";
            return false;
        }

        if (expectedExtraIngredientIds.Any(id => id < 0)
            || expectedExtraIngredientIds.Distinct().Count() != expectedExtraIngredientIds.Count)
        {
            diagnostic =
                "requested extra ingredients contain invalid or duplicate ids: "
                + $"{SpecialBusinessDiagnostics.FormatIds(expectedExtraIngredientIds)}";
            return false;
        }

        var expected = expectedExtraIngredientIds
            .OrderBy(id => id)
            .ToArray();
        var actual = rawActualExtraIngredientIds
            .OrderBy(id => id)
            .ToArray();
        if (!actual.SequenceEqual(expected))
        {
            diagnostic =
                $"extra ingredients mismatch; expected={SpecialBusinessDiagnostics.FormatIds(expected)}; "
                + $"actual={SpecialBusinessDiagnostics.FormatIds(actual)}";
            return false;
        }

        actualExtraIngredientIds = actual;
        diagnostic = $"extraIngredients={SpecialBusinessDiagnostics.FormatIds(actualExtraIngredientIds)}";
        return true;
    }
}
