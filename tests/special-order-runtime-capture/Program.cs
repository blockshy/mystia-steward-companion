using System.Reflection;
using MystiaStewardCompanion.Save;

try
{
    VerifyControllerTextResolvesNegativeBeverageTag();
    VerifyControllerOverrideIsAuthoritative();
    VerifyParsedOverrideMatchesByRawIdentity();
    VerifyOrderTextRemainsAvailableWithoutControllerGetter();
    VerifyMergeKeepsRawIdentityAndDisplayTextIndependent();
    VerifyDifferentRuntimeKeysNeverMerge();
    VerifyDismissMatchesEveryProvidedRuntimeIdentityField();
    VerifyFulfilledDeliveryStatusRemainsCaptured();
    Console.WriteLine("PASS: special-order capture keeps raw Tag identity separate from the game's final display text.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyControllerTextResolvesNegativeBeverageTag()
{
    var controller = new SpecialOrderController("甜", "无酒精");
    var captured = Parse(new SpecialOrder(17, -1, ""), controller);

    AssertEqual("甜", captured.FoodTagDisplayText, "Food Tag display text did not use the controller's final order text.");
    AssertEqual(17, captured.FoodTagId, "Food Tag ID changed unexpectedly.");
    AssertEqual(true, captured.HasFoodTagId, "A valid food Tag ID was discarded.");
    AssertEqual("无酒精", captured.BeverageTagDisplayText, "Negative beverage Tag ID lost its controller display text.");
    AssertEqual(-1, captured.BeverageTagId, "Negative beverage Tag ID changed unexpectedly.");
    AssertEqual(true, captured.HasBeverageTagId, "A negative beverage Tag ID was discarded even though the raw value was readable.");
    AssertEqual(1, controller.FoodReads, "Food order text was not read exactly once.");
    AssertEqual(1, controller.BeverageReads, "Beverage order text was not read exactly once.");
}

static void VerifyControllerOverrideIsAuthoritative()
{
    var controller = new SpecialOrderController("梦幻", "辛");
    var captured = Parse(new SpecialOrder(30, 14, BuildOrderText("旧料理", "旧酒水")), controller);

    AssertEqual(30, captured.FoodTagId, "Food Tag identity was replaced by controller display text.");
    AssertEqual(true, captured.HasFoodTagId, "Readable food Tag identity was discarded.");
    AssertEqual("梦幻", captured.FoodTagDisplayText, "Controller food override was replaced by stale order text.");
    AssertEqual(14, captured.BeverageTagId, "Beverage Tag identity was replaced by controller display text.");
    AssertEqual(true, captured.HasBeverageTagId, "Readable beverage Tag identity was discarded.");
    AssertEqual("辛", captured.BeverageTagDisplayText, "Controller beverage override was replaced by stale order text.");
}

static void VerifyParsedOverrideMatchesByRawIdentity()
{
    var captured = Parse(
        new SpecialOrder(30, 14, BuildOrderText("旧料理", "旧酒水")),
        new SpecialOrderController("料理就和魔法一样，发光发热才叫好！", "请给我可加冰的饮料"));
    var matched = RareOrderIdentityMatcher.Matches(
        new RareOrderIdentity(2, 123, 30, 14),
        new RareOrderIdentity(
            captured.DeskCode,
            captured.GuestId,
            captured.HasFoodTagId ? captured.FoodTagId : null,
            captured.HasBeverageTagId ? captured.BeverageTagId : null),
        out var reason);
    AssertEqual(true, matched, $"Controller display overrides changed the parsed runtime identity: {reason}");
}

static void VerifyOrderTextRemainsAvailableWithoutControllerGetter()
{
    var captured = Parse(new SpecialOrder(17, -1, BuildOrderText("甜", "无酒精")), new object());
    AssertEqual("甜", captured.FoodTagDisplayText, "Order food display text was not retained without a special-guest controller.");
    AssertEqual("无酒精", captured.BeverageTagDisplayText, "Order beverage display text was not retained without a special-guest controller.");
    AssertEqual(-1, captured.BeverageTagId, "Negative raw beverage Tag ID was not retained without a controller.");
    AssertEqual(true, captured.HasBeverageTagId, "Readable negative beverage Tag ID was marked as missing without a controller.");
}

static void VerifyMergeKeepsRawIdentityAndDisplayTextIndependent()
{
    var capturedAt = DateTime.UtcNow;
    var identityOnly = new CapturedRuntimeSpecialOrder(
        2,
        123,
        "Test guest",
        30,
        true,
        "灼热",
        -1,
        true,
        "无酒精",
        false,
        false,
        capturedAt,
        capturedAt,
        "",
        "Identity");
    var displayOnly = identityOnly with
    {
        FoodTagId = 0,
        HasFoodTagId = false,
        FoodTagDisplayText = "料理就和魔法一样，发光发热才叫好！",
        BeverageTagId = 0,
        HasBeverageTagId = false,
        BeverageTagDisplayText = "请给我可加冰的饮料",
        CaptureSource = "Display",
    };

    var merge = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "MergeCapturedOrder",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.MergeCapturedOrder was not found.");
    var merged = merge.Invoke(null, new object[] { displayOnly, identityOnly }) as CapturedRuntimeSpecialOrder
        ?? throw new InvalidOperationException("The special order captures were not merged.");

    AssertEqual(30, merged.FoodTagId, "Food Tag identity was lost while merging display text.");
    AssertEqual(true, merged.HasFoodTagId, "Merged food Tag identity was marked as missing.");
    AssertEqual("料理就和魔法一样，发光发热才叫好！", merged.FoodTagDisplayText, "Food display text was not merged independently.");
    AssertEqual(-1, merged.BeverageTagId, "Negative beverage Tag identity was lost while merging display text.");
    AssertEqual(true, merged.HasBeverageTagId, "Merged negative beverage Tag identity was marked as missing.");
    AssertEqual("请给我可加冰的饮料", merged.BeverageTagDisplayText, "Beverage display text was not merged independently.");
}

static void VerifyDifferentRuntimeKeysNeverMerge()
{
    var capturedAt = DateTime.UtcNow;
    var first = new CapturedRuntimeSpecialOrder(
        2, 123, "Test guest", 30, true, "灼热", -1, true, "无酒精",
        false, false, capturedAt, capturedAt, "ptr:1", "First");
    var second = first with { RuntimeKey = "ptr:2", CaptureSource = "Second" };
    var canMerge = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "CanMergeCapturedOrders",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.CanMergeCapturedOrders was not found.");
    var merged = canMerge.Invoke(null, new object[] { first, second }) as bool?;
    AssertEqual(false, merged, "Different native runtime keys were merged by desk/guest fallback.");

    var removalMatches = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "IsSameOrderRemovalMatch",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.IsSameOrderRemovalMatch was not found.");
    var removed = removalMatches.Invoke(null, new object[] { first, second }) as bool?;
    AssertEqual(false, removed, "A removal callback for another native runtime key matched this order.");
}

static void VerifyDismissMatchesEveryProvidedRuntimeIdentityField()
{
    var capturedAt = DateTime.UtcNow;
    var order = new CapturedRuntimeSpecialOrder(
        2, 123, "Test guest", 30, true, "灼热", -1, true, "无酒精",
        false, false, capturedAt, capturedAt, "ptr:1", "Test");
    var matches = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "IsDismissRequestMatch",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.IsDismissRequestMatch was not found.");

    bool Invoke(int? runtimeGuestId, int? foodTagId, int? beverageTagId) =>
        matches.Invoke(null, new object?[] { order, 2, runtimeGuestId, foodTagId, beverageTagId }) is true;

    AssertEqual(true, Invoke(123, 30, -1), "Complete runtime identity did not dismiss its own capture.");
    AssertEqual(false, Invoke(999, 30, -1), "Matching Tag IDs bypassed a conflicting runtime guest ID.");
    AssertEqual(false, Invoke(123, 31, -1), "Matching guest ID bypassed a conflicting food Tag ID.");
    AssertEqual(false, Invoke(123, 30, 14), "Matching guest and food IDs bypassed a conflicting beverage Tag ID.");
    AssertEqual(false, Invoke(null, null, null), "Desk-only dismissal was accepted without a runtime identity field.");

    var wrongDesk = matches.Invoke(null, new object?[] { order, 3, 123, 30, -1 }) is true;
    AssertEqual(false, wrongDesk, "A matching runtime identity bypassed a conflicting desk.");

    var missingFoodIdentity = order with { HasFoodTagId = false };
    var missingFieldMatched = matches.Invoke(null, new object?[] { missingFoodIdentity, 2, 123, 30, -1 }) is true;
    AssertEqual(false, missingFieldMatched, "A candidate missing a requested identity field was dismissed.");
}

static void VerifyFulfilledDeliveryStatusRemainsCaptured()
{
    var ordersField = typeof(SpecialOrderRuntimeCapture).GetField(
        "Orders",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.Orders was not found.");
    var orders = ordersField.GetValue(null) as System.Collections.IList
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.Orders was not a list.");
    var addOrder = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "AddOrder",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.AddOrder was not found.");
    var updateOrderStatus = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "UpdateOrderStatus",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.UpdateOrderStatus was not found.");

    orders.Clear();
    try
    {
        var capturedAt = DateTime.UtcNow;
        var pending = new CapturedRuntimeSpecialOrder(
            2, 123, "Test guest", 30, true, "灼热", -1, true, "无酒精",
            false, false, capturedAt, capturedAt, "ptr:fulfilled", "Test");
        var fulfilled = pending with { IsFulfilled = true, CaptureSource = "FoodDelivered" };
        addOrder.Invoke(null, new object?[] { pending });
        updateOrderStatus.Invoke(null, new object?[] { fulfilled, "FoodDelivered" });

        AssertEqual(1, orders.Count, "A fulfilled order was removed before its completion/evaluation stage.");
        var retained = orders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The retained capture had an unexpected type.");
        AssertEqual(true, retained.IsFulfilled, "The retained capture did not publish its fulfilled state.");
    }
    finally
    {
        orders.Clear();
    }
}

static CapturedRuntimeSpecialOrder Parse(SpecialOrder order, object controller)
{
    var parseOrder = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "ParseOrder",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.ParseOrder was not found.");
    return parseOrder.Invoke(null, new[] { order, "Smoke", controller }) as CapturedRuntimeSpecialOrder
        ?? throw new InvalidOperationException("The special order was not captured.");
}

static string BuildOrderText(string foodTag, string beverageTag)
{
    return string.Join('\n', new[]
    {
        "DeskCode: 2",
        "OrderType: Special",
        "ReqFoodTag:",
        $"  {foodTag}",
        "ReqBevTag:",
        $"  {beverageTag}",
        "Guest:",
        "  Test guest",
    });
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

internal sealed class SpecialOrder
{
    private readonly string _text;

    public SpecialOrder(int foodTagId, int beverageTagId, string text)
    {
        RequestFoodTag = foodTagId;
        RequestBeverageTag = beverageTagId;
        _text = text;
    }

    public string Type => "Special";
    public int DeskCode => 2;
    public int RequestFoodTag { get; }
    public int RequestBeverageTag { get; }
    public SpecialGuest SpecialGuests { get; } = new();

    public override string ToString() => _text;
}

internal sealed class SpecialGuest
{
    public int Id => 123;
    public string StringId => "Test guest";
}

internal sealed class SpecialOrderController
{
    private readonly string _foodTag;
    private readonly string _beverageTag;

    public SpecialOrderController(string foodTag, string beverageTag)
    {
        _foodTag = foodTag;
        _beverageTag = beverageTag;
    }

    public int FoodReads { get; private set; }
    public int BeverageReads { get; private set; }

    public string GetOrderFoodText(SpecialOrder order)
    {
        FoodReads++;
        return _foodTag;
    }

    public string GetOrderBevText(SpecialOrder order)
    {
        BeverageReads++;
        return _beverageTag;
    }
}
