namespace NightScene.GuestManagementUtility;

internal class GuestGroupController
{
    private static long _nextPointer;

    internal enum EvaluationResult
    {
        None,
    }

    internal enum LeaveType
    {
        Normal,
    }

    public GuestsManager.OrderBase? CurrentOrder { get; set; }
    public bool HasEvaluated { get; set; }
    public IntPtr Pointer { get; } = (IntPtr)Interlocked.Increment(ref _nextPointer);

    public GuestsManager.OrderBase? PeekOrders() => CurrentOrder;

    public void PushToOrder(GuestsManager.OrderBase order)
    {
    }
}

internal sealed class GuestsManager
{
    internal class OrderBase
    {
        private static long _nextPointer;

        protected OrderBase(int deskCode, bool manualOrder, bool freeOrder)
        {
            DeskCode = deskCode;
            ManualOrder = manualOrder;
            FreeOrder = freeOrder;
            Pointer = (IntPtr)Interlocked.Increment(ref _nextPointer);
        }

        public IntPtr Pointer { get; }
        public int DeskCode { get; }
        public bool ManualOrder { get; }
        public bool FreeOrder { get; }
    }

    internal sealed class NormalOrder : OrderBase
    {
        public NormalOrder(bool manualOrder = false)
            : base(2, manualOrder, freeOrder: false)
        {
        }

        public TestSellable RequestFood { get; } = new(17);
        public TestSellable RequestBeverage { get; } = new(21);
        public bool IsFullfilled { get; set; }
        public string Guest => "Test normal guest";
    }

    internal sealed class SpecialOrder : OrderBase
    {
        private readonly string _text;

        public SpecialOrder(int? foodTagId, int? beverageTagId, string text, bool manualOrder = false)
            : base(2, manualOrder, freeOrder: false)
        {
            RequestFoodTag = foodTagId;
            RequestBeverageTag = beverageTagId;
            _text = text;
        }

        public int? RequestFoodTag { get; set; }
        public int? RequestBeverageTag { get; set; }
        public bool IsFullfilled { get; set; }
        public SpecialGuest SpecialGuests { get; } = new();
        public int ToStringReads { get; private set; }

        public override string ToString()
        {
            ToStringReads++;
            return _text;
        }
    }

    private void SetManualControllerOrderInternal(
        GuestGroupController controller,
        Il2CppSystem.Action<GuestGroupController.EvaluationResult> onEvaluate,
        OrderBase order)
    {
    }

    public void AddToOrder(OrderBase order)
    {
    }

    public void RemoveFromOrder(OrderBase order)
    {
    }

    public void EvaluateOrder(GuestGroupController controller, bool triggeredByPartner, Il2CppSystem.Action onFinished)
    {
    }

    private void EvaulateManualOrder(
        GuestGroupController controller,
        Il2CppSystem.Action<GuestGroupController.EvaluationResult> onEvaluate)
    {
    }

    public void CleanOrderInfo(GuestGroupController controller)
    {
    }

    private void RepellInternal(
        GuestGroupController controller,
        out bool haveSeated,
        GuestGroupController.LeaveType leaveType,
        bool skipAnimation)
    {
        haveSeated = true;
    }
}

internal sealed record TestSellable(int id);

internal sealed class SpecialGuest
{
    public int Id => 123;
    public string StringId => "Test guest";
}

internal sealed class InvalidGuestsManager
{
    private void SetManualControllerOrderInternal(
        GuestGroupController controller,
        Il2CppSystem.Action onEvaluate,
        GuestsManager.OrderBase order)
    {
    }

    private void RepellInternal(
        GuestGroupController controller,
        ref bool haveSeated,
        GuestGroupController.LeaveType leaveType,
        bool skipAnimation)
    {
    }
}
