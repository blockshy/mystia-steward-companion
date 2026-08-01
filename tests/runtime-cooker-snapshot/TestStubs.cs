using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Core
{
    public sealed class DataRepository
    {
        public List<Recipe> Recipes { get; } = new();
        public List<Ingredient> Ingredients { get; } = new();
        public List<Beverage> Beverages { get; } = new();
        public Dictionary<int, int> RecipeIdToId { get; } = new();
    }
}

namespace MystiaStewardCompanion.Save
{
    internal sealed class RuntimeCookerControllerState
    {
        public object Cooker { get; init; } = null!;
        public IReadOnlyList<int> TypeIds { get; init; } = Array.Empty<int>();
        public bool IsEmptyDesk { get; init; }
        public int Phase { get; init; }
        public object? Result { get; init; }
        public object? ChosenRecipe { get; init; }
        public bool CouldOpen { get; init; }

        public bool ResultEmpty => Result == null;
        public bool ChosenRecipeEmpty => ChosenRecipe == null;
    }

    internal readonly record struct RuntimeCookerGridPosition(int X, int Y, int Z)
    {
        public override string ToString()
        {
            return $"{X},{Y},{Z}";
        }
    }

    internal sealed class RuntimeCookerControllerEntry
    {
        public object Controller { get; init; } = null!;
        public RuntimeCookerGridPosition GridPosition { get; init; }
        public string ControllerIdentity { get; init; } = "";
    }

    internal enum RuntimeCookingContentMutation
    {
        None,
        SetCook,
        Extract,
        Store,
    }

    internal readonly record struct RuntimeCookingOwnershipSnapshot(
        RuntimeCookingContentMutation LastMutation,
        bool MutationCompleted);

    internal static class RuntimeCookingGenerationTracker
    {
        public static Func<object, (bool Success, RuntimeCookingOwnershipSnapshot Snapshot, string Diagnostic)> OwnershipReader { get; set; }
            = _ => (false, default, "ownership=unavailable");

        public static bool TryGetOwnershipSnapshot(
            object cookController,
            out RuntimeCookingOwnershipSnapshot snapshot,
            out string diagnostic)
        {
            var result = OwnershipReader(cookController);
            snapshot = result.Snapshot;
            diagnostic = result.Diagnostic;
            return result.Success;
        }
    }

    internal static class RuntimeCookerReflection
    {
        public static Func<object?> ManagerReader { get; set; } = () => new object();
        public static Func<object?, (bool Success, IReadOnlyList<RuntimeCookerControllerEntry> Entries, string Status)> ControllerEntryReader { get; set; }
            = _ => (true, Array.Empty<RuntimeCookerControllerEntry>(), "allCookers=ok");
        public static Func<(bool Success, IReadOnlySet<RuntimeCookerGridPosition> Positions, string Status)> LockedPositionReader { get; set; }
            = () => (true, new HashSet<RuntimeCookerGridPosition>(), "lockedCookers=ok");
        public static Func<object, (bool Success, RuntimeCookerControllerState State, string Status)> StateReader { get; set; }
            = _ => (false, new RuntimeCookerControllerState(), "controller-state=not-configured");

        public static object? GetCookSystemManager()
        {
            return ManagerReader();
        }

        public static bool TryReadCookerControllerEntriesFromCookSystem(
            object? cookSystem,
            IReadOnlySet<RuntimeCookerGridPosition> lockedPositions,
            out IReadOnlyList<RuntimeCookerControllerEntry> entries,
            out string status)
        {
            var result = ControllerEntryReader(cookSystem);
            entries = result.Entries;
            status = result.Status;
            return result.Success;
        }

        public static bool TryReadLockedCookerPositions(
            out IReadOnlySet<RuntimeCookerGridPosition> positions,
            out string status)
        {
            var result = LockedPositionReader();
            positions = result.Positions;
            status = result.Status;
            return result.Success;
        }

        public static bool TryReadCookerControllerState(
            object controller,
            out RuntimeCookerControllerState state,
            out string status)
        {
            var result = StateReader(controller);
            state = result.State;
            status = result.Status;
            return result.Success;
        }

        public static string ResolveCookerTypeName(int typeId)
        {
            return typeId switch
            {
                1 => "煮锅",
                2 => "烧烤架",
                3 => "油锅",
                4 => "蒸锅",
                5 => "料理台",
                _ => $"#{typeId}",
            };
        }
    }
}
