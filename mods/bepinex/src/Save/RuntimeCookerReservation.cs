namespace MystiaStewardCompanion.Save;

internal enum RuntimeCookerChallengeGateState
{
    Available,
    Locked,
    Inconsistent,
}

internal readonly record struct RuntimeCookerReservation(
    int ControllerIndex,
    string ControllerIdentity,
    RuntimeCookerGridPosition GridPosition)
{
    public static bool TryCreate(
        int controllerIndex,
        string controllerIdentity,
        int? gridX,
        int? gridY,
        int? gridZ,
        out RuntimeCookerReservation reservation,
        out string error)
    {
        reservation = default;
        if (controllerIndex < 0)
        {
            error = "controller index is missing";
            return false;
        }

        if (!IsCanonicalControllerIdentity(controllerIdentity))
        {
            error = "controller identity is missing or invalid";
            return false;
        }

        if (!gridX.HasValue || !gridY.HasValue || !gridZ.HasValue)
        {
            error = "controller grid position is incomplete";
            return false;
        }

        reservation = new RuntimeCookerReservation(
            controllerIndex,
            controllerIdentity,
            new RuntimeCookerGridPosition(gridX.Value, gridY.Value, gridZ.Value));
        error = "";
        return true;
    }

    public bool TryMatch(
        IReadOnlyList<RuntimeCookerControllerEntry> currentEntries,
        out RuntimeCookerControllerEntry entry,
        out string error)
    {
        entry = null!;
        if (ControllerIndex >= currentEntries.Count)
        {
            error = $"controller index {ControllerIndex} is outside current range 0..{currentEntries.Count - 1}";
            return false;
        }

        var current = currentEntries[ControllerIndex];
        if (!string.Equals(
                current.ControllerIdentity,
                ControllerIdentity,
                StringComparison.Ordinal))
        {
            error = $"controller identity drifted from {ControllerIdentity} to {current.ControllerIdentity}";
            return false;
        }

        if (current.GridPosition != GridPosition)
        {
            error = $"controller position drifted from {GridPosition} to {current.GridPosition}";
            return false;
        }

        entry = current;
        error = "";
        return true;
    }

    public RuntimeCookerChallengeGateState EvaluateChallengeGate(
        IReadOnlySet<RuntimeCookerGridPosition> lockedPositions,
        bool couldOpen)
    {
        var challengeLocked = lockedPositions.Contains(GridPosition);
        if (couldOpen == challengeLocked)
        {
            return RuntimeCookerChallengeGateState.Inconsistent;
        }

        return challengeLocked
            ? RuntimeCookerChallengeGateState.Locked
            : RuntimeCookerChallengeGateState.Available;
    }

    private static bool IsCanonicalControllerIdentity(string value)
    {
        if (value.Length <= 2
            || value[0] != '0'
            || value[1] != 'x')
        {
            return false;
        }

        var nonZero = false;
        for (var index = 2; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= '1' and <= '9'
                || character is >= 'A' and <= 'F')
            {
                nonZero = true;
                continue;
            }

            if (character != '0')
            {
                return false;
            }
        }

        return nonZero;
    }
}
