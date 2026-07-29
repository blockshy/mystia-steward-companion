using System.Runtime.CompilerServices;

namespace BepInEx.Logging
{
    internal sealed class ManualLogSource
    {
        public List<string> Messages { get; } = new();

        public void LogInfo(object message)
        {
            Messages.Add($"Info:{message}");
        }

        public void LogWarning(object message)
        {
            Messages.Add($"Warning:{message}");
        }
    }
}

namespace MystiaStewardCompanion.Save
{
    internal enum NightBusinessLifecyclePhase
    {
        Inactive,
        Active,
        Closing,
        Destroyed,
    }

    internal sealed record NightBusinessLifecycleSnapshot(
        NightBusinessLifecyclePhase Phase,
        long Generation)
    {
        public bool IsActive => Phase == NightBusinessLifecyclePhase.Active;
    }

    internal static class RuntimeNightBusinessLifecycle
    {
        public static NightBusinessLifecycleSnapshot Snapshot { get; set; } =
            new(NightBusinessLifecyclePhase.Inactive, 0);
    }

    internal static class RuntimeReflectionUtility
    {
        public static Type? FindType(string fullName)
        {
            return string.Equals(
                fullName,
                "GameData.RunTime.Common.RunTimeStorage",
                StringComparison.Ordinal)
                ? typeof(GameData.RunTime.Common.RunTimeStorage)
                : null;
        }
    }

    internal static class AggregateModLogService
    {
        public static bool Enabled { get; set; }

        public static bool ThrowOnAppend { get; set; }

        public static List<AggregateEntry> Entries { get; } = new();

        public static void AppendSection(string channel, string title, string content)
        {
            if (ThrowOnAppend) throw new IOException("diagnostic sink failed");
            Entries.Add(new AggregateEntry(channel, title, content));
        }

        public static void Reset()
        {
            Enabled = false;
            ThrowOnAppend = false;
            Entries.Clear();
        }
    }

    internal sealed record AggregateEntry(string Channel, string Title, string Content);
}

namespace Il2CppSystem.Collections.Generic
{
    public interface IEnumerable<T>
    {
    }

    public sealed class Dictionary<TKey, TValue>
        where TKey : notnull
    {
    }
}

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppStructArray<T>
        where T : struct
    {
    }
}

namespace Il2CppSystem
{
    public delegate bool Predicate<T>(T value);
}

namespace GameData.RunTime.Common
{
    internal sealed record StorageCall(string Entry, int ObjectId, bool SuppressCallbacks);

    internal static class RuntimeStorageProbe
    {
        public static List<StorageCall> Calls { get; } = new();

        public static bool ThrowOnNextObjectOut { get; set; }

        public static void Reset()
        {
            Calls.Clear();
            ThrowOnNextObjectOut = false;
        }
    }

    public static class RunTimeStorage
    {
        private static readonly Il2CppSystem.Collections.Generic.Dictionary<int, int> Storage = new();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void BadgeOut(int badgeId, bool suppressCallbacks)
        {
            ObjectOut(Storage, badgeId, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void BadgeOutRange(
            Il2CppSystem.Collections.Generic.IEnumerable<int> badgeIds,
            bool suppressCallbacks)
        {
            ObjectOut(Storage, -1, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void BeverageOut(int beverageId, bool suppressCallbacks)
        {
            ObjectOut(Storage, beverageId, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void BeverageOutRange(
            Il2CppSystem.Collections.Generic.IEnumerable<int> beverageIds,
            bool suppressCallbacks)
        {
            ObjectOut(Storage, -1, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CookerOut(int cookerId, bool suppressCallbacks)
        {
            ObjectOut(Storage, cookerId, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CookerOutRange(
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> cookerIds,
            bool suppressCallbacks)
        {
            ObjectOut(Storage, -1, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FoodOut(int foodId, bool suppressCallbacks)
        {
            ObjectOut(Storage, foodId, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void FoodOutRange(
            Il2CppSystem.Collections.Generic.IEnumerable<int> foodIds,
            bool suppressCallbacks)
        {
            ObjectOut(Storage, -1, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void IngredientOut(int ingredientId, bool suppressCallbacks)
        {
            ObjectOut(Storage, ingredientId, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void IngredientOutRange(
            Il2CppSystem.Collections.Generic.IEnumerable<int> ingredientIds,
            bool suppressCallbacks)
        {
            ObjectOut(Storage, -1, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ItemOut(int itemId, bool suppressCallbacks)
        {
            ObjectOut(Storage, itemId, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ItemOutRange(
            Il2CppSystem.Collections.Generic.IEnumerable<int> itemIds,
            bool suppressCallbacks)
        {
            ObjectOut(Storage, -1, suppressCallbacks, null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ObjectOut(
            Il2CppSystem.Collections.Generic.Dictionary<int, int> objectPool,
            int objectId,
            bool suppressCallbacks,
            Il2CppSystem.Predicate<int>? infiniteResolver)
        {
            RuntimeStorageProbe.Calls.Add(new StorageCall(
                ResolveEntry(),
                objectId,
                suppressCallbacks));
            if (!RuntimeStorageProbe.ThrowOnNextObjectOut) return;

            RuntimeStorageProbe.ThrowOnNextObjectOut = false;
            throw new InvalidOperationException("native storage failure");
        }

        private static string ResolveEntry()
        {
            var frame = new System.Diagnostics.StackTrace().GetFrame(2);
            return frame?.GetMethod()?.Name ?? "unknown";
        }
    }

    internal sealed class TestEnumerable<T> : Il2CppSystem.Collections.Generic.IEnumerable<T>
    {
    }
}
