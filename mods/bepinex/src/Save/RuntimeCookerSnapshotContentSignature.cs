using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerSnapshotContentSignature
{
    public static int Append(int seed, RecommendationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        unchecked
        {
            var hash = HashIds(seed, state.PlacedCookerTypeIds);
            hash = (hash * 31) + (state.PlacedCookerSnapshotComplete ? 1 : 0);
            hash = (hash * 31) + state.PlacedCookerControllerCount;
            hash = (hash * 31) + state.PlacedCookerEmptyControllerCount;
            hash = (hash * 31) + state.PlacedCookerLockedControllerCount;
            hash = (hash * 31) + state.PlacedCookerReadFailureCount;
            foreach (var cooker in state.PlacedCookers.OrderBy(cooker => cooker.ControllerIndex))
            {
                hash = (hash * 31) + cooker.ControllerIndex;
                hash = (hash * 31) + cooker.GridPosition.X;
                hash = (hash * 31) + cooker.GridPosition.Y;
                hash = (hash * 31) + cooker.GridPosition.Z;
                hash = (hash * 31) + cooker.ControllerIdentity.GetHashCode();
                hash = HashIds(hash, cooker.TypeIds);
                hash = (hash * 31) + (cooker.ChallengeLocked ? 1 : 0);
                hash = (hash * 31) + (cooker.CouldOpen ? 1 : 0);
                hash = (hash * 31) + (cooker.AutomationAvailable ? 1 : 0);
                hash = (hash * 31) + cooker.AutomationAvailability.GetHashCode();
                hash = (hash * 31) + cooker.AutomationAvailabilityDiagnostic.GetHashCode();
                hash = (hash * 31) + cooker.Source.GetHashCode();
            }

            hash = (hash * 31) + state.PlacedCookerStatus.GetHashCode();
            return hash;
        }
    }

    private static int HashIds(int seed, IEnumerable<int> values)
    {
        unchecked
        {
            var hash = seed;
            foreach (var value in values.OrderBy(value => value))
            {
                hash = (hash * 31) + value;
            }

            return hash;
        }
    }
}
