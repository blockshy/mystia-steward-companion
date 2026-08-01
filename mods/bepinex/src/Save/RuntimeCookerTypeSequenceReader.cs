namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerTypeSequenceReader
{
    private const int MaxCookerTypeCount = 32;
    private const int MaxExceptionDiagnosticLength = 160;

    public static bool TryRead(
        Func<object?> moveNext,
        Func<object?> getCurrent,
        Type expectedElementType,
        Action dispose,
        out IReadOnlyList<int> typeIds,
        out bool observedEmpty,
        out string status)
    {
        ArgumentNullException.ThrowIfNull(moveNext);
        ArgumentNullException.ThrowIfNull(getCurrent);
        ArgumentNullException.ThrowIfNull(expectedElementType);
        ArgumentNullException.ThrowIfNull(dispose);

        var result = new List<int>();
        var seen = new HashSet<int>();
        observedEmpty = false;
        var observedAny = false;
        var completed = false;
        var failure = "";

        for (var index = 0; index <= MaxCookerTypeCount; index++)
        {
            object? moved;
            try
            {
                moved = moveNext();
            }
            catch (Exception ex)
            {
                failure = $"cooker-types=move-next-invoke-failed; index={index}; error={FormatException(ex)}";
                break;
            }

            if (moved is not bool hasNext)
            {
                failure = $"cooker-types=move-next-value-invalid; index={index}; "
                    + $"actual={moved?.GetType().FullName ?? "null"}";
                break;
            }

            if (!hasNext)
            {
                completed = true;
                break;
            }

            if (index == MaxCookerTypeCount)
            {
                failure = $"cooker-types=sequence-limit-exceeded; limit={MaxCookerTypeCount}";
                break;
            }

            object? current;
            try
            {
                current = getCurrent();
            }
            catch (Exception ex)
            {
                failure = $"cooker-types=current-invoke-failed; index={index}; error={FormatException(ex)}";
                break;
            }

            if (current == null || current.GetType() != expectedElementType)
            {
                failure = $"cooker-types=current-shape-invalid; index={index}; "
                    + $"declared={expectedElementType.FullName}; actual={current?.GetType().FullName ?? "null"}";
                break;
            }

            int typeId;
            try
            {
                typeId = Convert.ToInt32(current);
            }
            catch (Exception ex)
            {
                failure = $"cooker-types=current-conversion-failed; index={index}; error={FormatException(ex)}";
                break;
            }

            observedAny = true;
            if (typeId == 0)
            {
                observedEmpty = true;
                continue;
            }

            if (typeId is < 1 or > 5)
            {
                failure = $"cooker-types=value-out-of-range; index={index}; value={typeId}";
                break;
            }

            if (seen.Add(typeId)) result.Add(typeId);
        }

        try
        {
            dispose();
        }
        catch (Exception ex)
        {
            if (failure.Length == 0)
            {
                failure = $"cooker-types=dispose-failed; error={FormatException(ex)}";
            }
        }

        if (failure.Length == 0 && !completed)
        {
            failure = $"cooker-types=sequence-limit-exceeded; limit={MaxCookerTypeCount}";
        }

        if (failure.Length == 0 && !observedAny)
        {
            failure = "cooker-types=sequence-empty";
        }

        if (failure.Length > 0)
        {
            typeIds = Array.Empty<int>();
            observedEmpty = false;
            status = failure;
            return false;
        }

        result.Sort();
        typeIds = result;
        status = $"cooker-types=complete; empty={observedEmpty}; capabilities={string.Join(",", result)}";
        return true;
    }

    public static bool TryValidateControllerState(
        bool isEmptyDesk,
        bool observedEmpty,
        int capabilityCount,
        int phase,
        bool resultEmpty,
        bool chosenRecipeEmpty,
        out string status)
    {
        if (isEmptyDesk && (!observedEmpty || capabilityCount != 0))
        {
            status = "empty-desk-type-mismatch";
            return false;
        }

        if (isEmptyDesk && (phase != 0 || !resultEmpty || !chosenRecipeEmpty))
        {
            status = "empty-desk-content-mismatch";
            return false;
        }

        if (!isEmptyDesk && capabilityCount == 0)
        {
            status = "non-empty-desk-types-missing";
            return false;
        }

        status = isEmptyDesk ? "ok-empty-desk" : "ok";
        return true;
    }

    public static string FormatException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var cause = exception.GetBaseException();
        var value = $"{cause.GetType().Name}:{cause.Message.Replace('\r', ' ').Replace('\n', ' ')}";
        if (value.Length <= MaxExceptionDiagnosticLength) return value;
        return value[..(MaxExceptionDiagnosticLength - 3)] + "...";
    }
}
