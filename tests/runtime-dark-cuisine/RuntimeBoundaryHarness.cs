using System.Reflection;
using MystiaStewardCompanion.LocalApi;

// 本宿主直接链接生产 partial，只替换 Unity/IL2CPP 和既有送达事务边界。
// 它验证调用顺序与拒绝策略，不把托管替身当作原生反射、游戏回调或实际扣料证据。
namespace MystiaStewardCompanion.LocalApi
{
    internal sealed class OrderPreparationRequest
    {
        public int FoodId = -1;
        public string FoodProductionKind = "dark-cuisine";
        public bool AutoCompleteOrder = true, AutoCollectCooking = true, AutoDeliverFood = false;
    }
    internal sealed class OrderPreparationResult
    {
        public bool ServedFood;
        public State Automation = new();
        public List<OrderPreparationStep> Steps = new();
        internal sealed class State { public string Stage = ""; }
    }
    internal sealed class OrderPreparationStep
    {
        public string Code = "", Name = "", Message = "";
        public bool Ok;
    }
    internal static class OrderPreparationStepCodes
    {
        public const string FoodDelivered = "delivered", CookingCookerWaiting = "waiting", FoodDeliveryCommitUncertain = "uncertain";
    }
}
namespace NightScene.EventUtility
{
    internal sealed class EventManager
    {
        public enum BuffType { Medicine_Positive = 110 }
        public bool Protected, ThrowOnRead;
        public object registeredOverrideFoodBuff = new List<object>();
        public int RandomOverrideCalls;
        public bool CheckTimedBuffExists(BuffType buff)
            => ThrowOnRead ? throw new InvalidOperationException("unreadable buff") : Protected;
        // 若生产代码错误地用随机替换入口作查询，测试必须失败。
        public void GetOverrideFood() { RandomOverrideCalls++; throw new InvalidOperationException("random read forbidden"); }
    }
}
namespace MystiaStewardCompanion.Save
{
    internal enum RuntimeMissionDiagnosticPhase { Ready, Unavailable }
    internal sealed record MissionSnapshot(bool RuntimeAvailable = true, RuntimeMissionDiagnosticPhase Phase = RuntimeMissionDiagnosticPhase.Ready,
        long Generation = 7, int DefinitionFailureCount = 0);
    internal sealed record Signal(int CanonicalGuestId = 3, int RawGuestId = 1003, int FoodId = -1, long MissionGeneration = 7, long BusinessGeneration = 9);
    internal sealed record SignalSnapshot(bool HookAttached, long MissionGeneration, long BusinessGeneration, string NightPhase, IReadOnlyList<Signal> Signals);
    internal static class RuntimeMissionDiagnosticCapture { public static MissionSnapshot Snapshot() => RuntimeOrderPreparationService.Current.Mission; }
    internal static class RuntimeServeInWorkMissionDiagnosticCapture { public static SignalSnapshot Snapshot() => RuntimeOrderPreparationService.Current.Signals; }
    internal static class RuntimeConcreteCollectionReader
    {
        public static bool TryReadIntArray(object? source, out IReadOnlyList<int> values, out string error)
        { error = ""; values = source as int[] ?? Array.Empty<int>(); return source is int[]; }
        public static bool TryReadList(object? source, out IReadOnlyList<object?> values, out string error)
        { error = ""; values = source as List<object> ?? new List<object>(); return source is List<object>; }
    }
    internal static partial class RuntimeOrderPreparationService
    {
        internal sealed class Scenario
        {
            public MissionSnapshot Mission = new();
            public SignalSnapshot Signals = new(true, 7, 9, "Active", new[] { new Signal() });
            public NightScene.EventUtility.EventManager Effects = new();
            public FakeStorage Storage = new();
            public CookingCollectionTarget Target = new();
            public OrderPreparationRequest Request = new();
            public RuntimeOrderMatch Order = new();
            public bool Lifecycle = true, Binding = true, ExistingJob, StoreSucceeds = true;
            public string Delivery = "committed";
            public int DeliveryCalls, EvaluationCalls, Barriers, FindOrderCalls, ReturnCalls;
            public List<string> Events = new();
        }
        internal sealed class FakeFood
        {
            public int Id = -1;
            public nint Pointer = 42;
            public object Modifier = new[] { 2 };
            public object banTags = new[] { 8 };
        }
        internal sealed class FakeRecipe { public int foodID = 5; }
        internal sealed class FakeIngredient { public int id = 2; public object Tags = new[] { 8 }; }
        internal sealed class FakeStorage
        {
            public List<object> Items = new();
            public int Removes;
            public Action? AfterRemove;
            public bool ThrowAfterRemove;
            public void RemoveStoredFood(FakeFood food, int partner)
            {
                Current.Events.Add("remove"); Removes++; Items.Remove(food); AfterRemove?.Invoke();
                if (ThrowAfterRemove) throw new InvalidOperationException("callback failed");
            }
        }
        internal enum CookingCollectionTargetKind { RareOrder, NormalOrder }
        internal readonly record struct BindingToken(long BusinessGeneration);
        internal sealed class CookingCollectionTarget
        {
            public CookingCollectionTargetKind Kind = CookingCollectionTargetKind.RareOrder;
            public int FoodId = -1;
            public string FoodProductionKind = "dark-cuisine", FoodName = "黑暗料理";
            public BindingToken? OrderBinding = new(9);
            public int? GuestId = 3, RuntimeGuestId = 1003;
            public long MissionGeneration = 7;
            public int[] ExtraIngredientIds = new[] { 2 };
        }
        internal sealed class RuntimeOrderMatch { public object? Order = new(), Controller = new(); }
        internal sealed class FakeJob { public nint CurrentResultPointer; }
        internal readonly record struct FoodIdentity(int FoodId) { public bool IsDarkCuisine => FoodId == -1; }
        internal readonly record struct DeliveryResult(bool Ok, bool CommitUncertain, string Message);
        internal enum RuntimeDeliveryItemKind { Food }
        internal static Scenario Current = new();
        private const string IzakayaConfigureTypeName = "FakeStorage", DataBaseCoreTypeName = "FakeDatabase";
        private static readonly object AutomationCookingJobLock = new();
        private static readonly List<FakeJob> AutomationCookingJobs = new();
        internal static Scenario Reset()
        {
            Current = new(); AutomationCookingJobs.Clear(); return Current;
        }
        internal static (bool Accepted, string Reason) ValidateProduction(FakeRecipe? recipe = null, FakeFood? initial = null)
        {
            var accepted = TryValidateDarkCuisineProduction(Current.Target, recipe ?? new FakeRecipe(), initial ?? new FakeFood { Id = 5 }, out var reason);
            return (accepted, reason);
        }
        internal static (bool Handled, OrderPreparationResult Result) TakeStock(bool requireStock = true)
        {
            var result = new OrderPreparationResult();
            var handled = TryUseStoredDarkCuisine(Current.Request, result, Current.Target, Current.Order, requireStock);
            return (handled, result);
        }
        internal static void OwnStock(nint pointer) => AutomationCookingJobs.Add(new FakeJob { CurrentResultPointer = pointer });
        private static bool TryReadCookControllerFoodResultIdentity(object food, string source, out FoodIdentity identity, out string error)
        { error = ""; identity = new(food is FakeFood item ? item.Id : -2); return food is FakeFood && identity.FoodId >= -1; }
        private static bool TryReadNativeObjectPointer(object food, out nint pointer)
        { pointer = food is FakeFood item ? item.Pointer : 0; return pointer != 0; }
        private static object? ReadExactMember(object source, string member) => source.GetType().GetField(member)?.GetValue(source);
        private static object? GetSingletonInstance(string name) => name == IzakayaConfigureTypeName ? Current.Storage : Current.Effects;
        private static Type? FindType(string name) => typeof(NightScene.EventUtility.EventManager).Assembly.GetType(name);
        private static object? InvokeStatic(string name, string method, object?[] args) => new FakeIngredient { id = (int)args[0]! };
        private static object ReadStoredFoodList(object configure) => ((FakeStorage)configure).Items;
        private static bool TryFindAutomationCookingJob(CookingCollectionTarget target, out object? job, out string diagnostic)
        { job = null; diagnostic = ""; return Current.ExistingJob; }
        private static bool TryMatchRuntimeOrderBinding(BindingToken? binding, object order, object controller, out string diagnostic)
        { diagnostic = ""; return Current.Binding; }
        private static bool TryValidateCookingTargetOrderLifecycle(CookingCollectionTarget target, string phase, out string diagnostic)
        { diagnostic = ""; Current.Events.Add("lifecycle"); return Current.Lifecycle; }
        private static bool TryInspectStoredFoodIdentity(object configure, object food, out bool stored, out string diagnostic)
        { diagnostic = ""; stored = ((FakeStorage)configure).Items.Contains(food); Current.Events.Add("inspect"); return true; }
        private static bool TryReadOrderServedItem(object order, RuntimeDeliveryItemKind kind, out object? served, out string diagnostic)
        { diagnostic = ""; served = null; return true; }
        private static bool TryReadOrderInAirItem(object order, RuntimeDeliveryItemKind kind, out object? served, out string diagnostic)
        { diagnostic = ""; served = null; return true; }
        private static RuntimeOrderMatch FindRuntimeOrder(OrderPreparationRequest request)
        { Current.FindOrderCalls++; return Current.Order; }
        private static DeliveryResult TryCommitRuntimeDelivery(RuntimeOrderMatch order, object food, RuntimeDeliveryItemKind kind, string name)
        { Current.DeliveryCalls++; return new(Current.Delivery == "committed", Current.Delivery == "uncertain", Current.Delivery); }
        private static bool TryInvokeStoreFood(object configure, object food, out object? ignored, out string diagnostic)
        {
            ignored = null; diagnostic = ""; Current.ReturnCalls++;
            if (Current.StoreSucceeds) ((FakeStorage)configure).Items.Add(food);
            return Current.StoreSucceeds;
        }
        private static void TryEvaluateMatchedAutomationOrderIfReady(OrderPreparationResult result, OrderPreparationRequest request,
            RuntimeOrderMatch order, string action, string label, CookingCollectionTarget target) => Current.EvaluationCalls++;
        private static void AddSkipped(OrderPreparationResult result, string name, string message, string code)
            => result.Steps.Add(new() { Code = code, Name = name, Message = message });
        private static void AddFailure(OrderPreparationResult result, string name, string message, string code)
            => result.Steps.Add(new() { Code = code, Name = name, Message = message });
        private static void RecordOrderSafetyBarrierIfNeeded(string code, CookingCollectionTarget target, string message) => Current.Barriers++;
        private static class InvocationMetadata
        {
            public static MethodInfo? FindExactMethod(Type type, string name, BindingFlags flags, Type[] args)
                => type.GetMethod(name, flags, null, args, null);
        }
    }
}