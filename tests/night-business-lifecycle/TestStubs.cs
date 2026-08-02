using System.Reflection;

namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public void LogInfo(object data) { }
        public void LogWarning(object data) { }
    }
}

namespace HarmonyLib
{
    public static class Priority
    {
        public const int First = 800;
        public const int Last = 0;
    }

    public sealed class HarmonyMethod
    {
        public HarmonyMethod(MethodInfo method) { }
        public int priority { get; set; }
    }

    public sealed class Harmony
    {
        public Harmony(string id) { }

        public void Patch(
            MethodBase original,
            HarmonyMethod? prefix = null,
            HarmonyMethod? postfix = null)
        {
        }
    }
}

namespace MystiaStewardCompanion.Save
{
    internal static class RuntimeBoundaryProbe
    {
        public static int CookerResumeCount { get; set; }
        public static int CookerSuspendCount { get; set; }
        public static int CookerAbandonCount { get; set; }
        public static int SeatResumeCount { get; set; }
        public static int SeatSuspendCount { get; set; }
        public static int SeatAbandonCount { get; set; }
        public static int OrderResumeCount { get; set; }
        public static int OrderSuspendCount { get; set; }
        public static int OrderAbandonCount { get; set; }
        public static int ListResumeCount { get; set; }
        public static int ListSuspendCount { get; set; }
        public static int ListAbandonCount { get; set; }
        public static int TargetInvalidationCount { get; set; }
        public static long LastInvalidatedGeneration { get; set; }
        public static int SpecialOrderClearCount { get; set; }
        public static int NormalOrderClearCount { get; set; }
        public static int SpecialBusinessClearCount { get; set; }
        public static int CookingGenerationClearCount { get; set; }
        public static int CookingJobClearCount { get; set; }
        public static int ServeInWorkBoundaryCount { get; set; }
        public static NightBusinessLifecyclePhase LastServeInWorkPhase { get; set; }

        public static void Reset()
        {
            CookerResumeCount = 0;
            CookerSuspendCount = 0;
            CookerAbandonCount = 0;
            SeatResumeCount = 0;
            SeatSuspendCount = 0;
            SeatAbandonCount = 0;
            OrderResumeCount = 0;
            OrderSuspendCount = 0;
            OrderAbandonCount = 0;
            ListResumeCount = 0;
            ListSuspendCount = 0;
            ListAbandonCount = 0;
            TargetInvalidationCount = 0;
            LastInvalidatedGeneration = 0;
            SpecialOrderClearCount = 0;
            NormalOrderClearCount = 0;
            SpecialBusinessClearCount = 0;
            CookingGenerationClearCount = 0;
            CookingJobClearCount = 0;
            ServeInWorkBoundaryCount = 0;
            LastServeInWorkPhase = NightBusinessLifecyclePhase.Inactive;
        }
    }

    internal static class RuntimeReflectionUtility
    {
        public static Type? FindType(string fullName) => null;
    }

    internal static class RuntimeCookerHighlightService
    {
        public static void Resume(string reason) => RuntimeBoundaryProbe.CookerResumeCount++;
        public static void Suspend(string reason) => RuntimeBoundaryProbe.CookerSuspendCount++;
        public static void Abandon(string reason) => RuntimeBoundaryProbe.CookerAbandonCount++;
    }

    internal static class RuntimeSeatHighlightService
    {
        public static void Resume(string reason) => RuntimeBoundaryProbe.SeatResumeCount++;
        public static void Suspend(string reason) => RuntimeBoundaryProbe.SeatSuspendCount++;
        public static void Abandon(string reason) => RuntimeBoundaryProbe.SeatAbandonCount++;
    }

    internal static class RuntimeOrderHighlightService
    {
        public static void Resume(string reason) => RuntimeBoundaryProbe.OrderResumeCount++;
        public static void Suspend(string reason) => RuntimeBoundaryProbe.OrderSuspendCount++;
        public static void Abandon(string reason) => RuntimeBoundaryProbe.OrderAbandonCount++;
    }

    internal static class RuntimeServeInWorkMissionDiagnosticCapture
    {
        public static void ApplyBusinessBoundary(
            NightBusinessLifecycleSnapshot snapshot,
            DateTime changedAtUtc)
        {
            RuntimeBoundaryProbe.ServeInWorkBoundaryCount++;
            RuntimeBoundaryProbe.LastServeInWorkPhase = snapshot.Phase;
        }
    }

    internal static class RuntimePinnedListHighlightService
    {
        public static void Resume(string reason) => RuntimeBoundaryProbe.ListResumeCount++;
        public static void Suspend(string reason) => RuntimeBoundaryProbe.ListSuspendCount++;
        public static void Abandon(string reason) => RuntimeBoundaryProbe.ListAbandonCount++;
    }

    internal static class RuntimeUiPinningService
    {
        public static void InvalidateTarget(long generation, string reason)
        {
            RuntimeBoundaryProbe.TargetInvalidationCount++;
            RuntimeBoundaryProbe.LastInvalidatedGeneration = generation;
        }
    }

    internal static class SpecialOrderRuntimeCapture
    {
        public static void ClearOrders(string reason) => RuntimeBoundaryProbe.SpecialOrderClearCount++;
    }

    internal static class NormalOrderRuntimeCapture
    {
        public static void ClearOrders(string reason) => RuntimeBoundaryProbe.NormalOrderClearCount++;
    }

    internal static class RuntimeSpecialBusinessContextService
    {
        public static void ClearForBusinessEnd(string reason) => RuntimeBoundaryProbe.SpecialBusinessClearCount++;
    }

    internal static class RuntimeCookingGenerationTracker
    {
        public static void ClearForSceneChange() => RuntimeBoundaryProbe.CookingGenerationClearCount++;
    }

    internal static class RuntimeOrderPreparationService
    {
        public static void ClearAutomationCookingJobs(string reason) => RuntimeBoundaryProbe.CookingJobClearCount++;
    }
}
