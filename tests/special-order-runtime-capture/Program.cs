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
    VerifyManualSetterHookUsesExact783Signature();
    VerifyManualSettersBindExactManualStateAndCallbacks();
    VerifyLatestManualStateOwnsMergedCallback();
    VerifySkippedManualSetterDoesNotCapture();
    VerifyOrdinaryOrderIsNotAParseFailure();
    Console.WriteLine("PASS: special-order capture keeps raw Tag identity separate from the game's final display text.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyManualSetterHookUsesExact783Signature()
{
    var exact = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
        "SetManualControllerOrderInternal",
        BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("The exact manual-order setter fixture was not found.");
    var wrong = typeof(NightScene.GuestManagementUtility.InvalidGuestsManager).GetMethod(
        "SetManualControllerOrderInternal",
        BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("The invalid manual-order setter fixture was not found.");

    foreach (var captureType in new[]
             {
                 typeof(SpecialOrderRuntimeCapture),
                 typeof(NormalOrderRuntimeCapture),
             })
    {
        var predicate = captureType.GetMethod(
            "IsExactManualOrderSetter",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{captureType.Name}.IsExactManualOrderSetter was not found.");
        AssertEqual(
            true,
            predicate.Invoke(null, new object[] { exact }) is true,
            $"{captureType.Name} rejected the exact BepInEx 783 manual-order setter signature.");
        AssertEqual(
            false,
            predicate.Invoke(null, new object[] { wrong }) is true,
            $"{captureType.Name} accepted a manual-order setter with the wrong callback type.");
    }
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

static void VerifyManualSettersBindExactManualStateAndCallbacks()
{
    var specialOrders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var normalOrders = GetCaptureOrders(typeof(NormalOrderRuntimeCapture));
    var specialSetter = GetManualSetter(typeof(SpecialOrderRuntimeCapture));
    var normalSetter = GetManualSetter(typeof(NormalOrderRuntimeCapture));
    var specialCallback = new object();
    var normalCallback = new object();

    specialOrders.Clear();
    normalOrders.Clear();
    try
    {
        specialSetter.Invoke(
            null,
            new object?[]
            {
                new SpecialOrderController("甜", "无酒精"),
                specialCallback,
                new SpecialOrder(17, -1, "", manualOrder: true),
                true,
            });
        AssertEqual(1, specialOrders.Count, "The special manual setter did not capture its order.");
        var capturedSpecial = specialOrders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The special manual capture had an unexpected type.");
        AssertEqual(true, capturedSpecial.ManualOrder, "The exact special OrderBase.ManualOrder state was lost.");
        AssertEqual(
            true,
            ReferenceEquals(specialCallback, capturedSpecial.ManualEvaluationCallback),
            "The special manual callback was not the original SetManualControllerOrderInternal __1 object.");

        normalSetter.Invoke(
            null,
            new object?[]
            {
                new object(),
                normalCallback,
                new CapturedNormalOrderFixture(manualOrder: true),
                true,
            });
        AssertEqual(1, normalOrders.Count, "The normal manual setter did not capture its order.");
        var capturedNormal = normalOrders[0] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The normal manual capture had an unexpected type.");
        AssertEqual(true, capturedNormal.ManualOrder, "The exact normal OrderBase.ManualOrder state was lost.");
        AssertEqual(
            true,
            ReferenceEquals(normalCallback, capturedNormal.ManualEvaluationCallback),
            "The normal manual callback was not the original SetManualControllerOrderInternal __1 object.");

        specialOrders.Clear();
        specialSetter.Invoke(
            null,
            new object?[]
            {
                new SpecialOrderController("甜", "无酒精"),
                null,
                new SpecialOrder(17, -1, "", manualOrder: true),
                true,
            });
        var missingCallback = specialOrders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The incomplete special manual capture had an unexpected type.");
        AssertEqual(true, missingCallback.ManualOrder, "A missing callback erased the manual-order identity.");
        AssertEqual<object?>(null, missingCallback.ManualEvaluationCallback, "A missing callback was replaced by a fallback object.");

        normalOrders.Clear();
        normalSetter.Invoke(
            null,
            new object?[]
            {
                new object(),
                null,
                new CapturedNormalOrderFixture(manualOrder: true),
                true,
            });
        var missingNormalCallback = normalOrders[0] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The incomplete normal manual capture had an unexpected type.");
        AssertEqual(true, missingNormalCallback.ManualOrder, "A missing callback erased the normal manual-order identity.");
        AssertEqual<object?>(null, missingNormalCallback.ManualEvaluationCallback, "A missing normal callback was replaced by a fallback object.");
    }
    finally
    {
        specialOrders.Clear();
        normalOrders.Clear();
    }
}

static void VerifyLatestManualStateOwnsMergedCallback()
{
    var capturedAt = DateTime.UtcNow;
    var callback = new object();
    var specialMerge = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "MergeCapturedOrder",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.MergeCapturedOrder was not found.");
    var specialManual = new CapturedRuntimeSpecialOrder(
        2, 1003, "Yuuma", 30, true, "灼热", -1, true, "无酒精",
        false, false, capturedAt, capturedAt, "ptr:reused", "ManualOrderSet")
    {
        OrderObject = new object(),
        ControllerObject = new object(),
        ManualOrder = true,
        ManualEvaluationCallback = callback,
    };
    var specialStillManual = specialManual with
    {
        CaptureSource = "OrderAdd",
        ManualEvaluationCallback = null,
    };
    var mergedSpecialManual = specialMerge.Invoke(
        null,
        new object?[] { specialStillManual, specialManual }) as CapturedRuntimeSpecialOrder
        ?? throw new InvalidOperationException("The merged special manual order had an unexpected type.");
    AssertEqual(true, mergedSpecialManual.ManualOrder, "A later exact ManualOrder=true capture was lost.");
    AssertEqual(
        true,
        ReferenceEquals(callback, mergedSpecialManual.ManualEvaluationCallback),
        "A later manual status capture discarded the callback from the same manual order.");

    var mergedSpecialStandard = specialMerge.Invoke(
        null,
        new object?[]
        {
            specialManual with { CaptureSource = "OrderAdd", ManualOrder = false, ManualEvaluationCallback = null },
            specialManual,
        }) as CapturedRuntimeSpecialOrder
        ?? throw new InvalidOperationException("The merged special standard order had an unexpected type.");
    AssertEqual(false, mergedSpecialStandard.ManualOrder, "A reused special-order slot retained stale ManualOrder=true.");
    AssertEqual<object?>(null, mergedSpecialStandard.ManualEvaluationCallback, "A reused special-order slot retained a stale callback.");

    var normalMerge = typeof(NormalOrderRuntimeCapture).GetMethod(
        "MergeCapturedOrder",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("NormalOrderRuntimeCapture.MergeCapturedOrder was not found.");
    var normalManual = new CapturedRuntimeNormalOrder(
        "ptr:reused", 2, "Yuuma", 17, 3, capturedAt, capturedAt, "ManualOrderSet")
    {
        OrderObject = new object(),
        ControllerObject = new object(),
        ManualOrder = true,
        ManualEvaluationCallback = callback,
    };
    var mergedNormalManual = normalMerge.Invoke(
        null,
        new object?[]
        {
            normalManual with { CaptureSource = "OrderAdd", ManualEvaluationCallback = null },
            normalManual,
        }) as CapturedRuntimeNormalOrder
        ?? throw new InvalidOperationException("The merged normal manual order had an unexpected type.");
    AssertEqual(true, mergedNormalManual.ManualOrder, "A later normal ManualOrder=true capture was lost.");
    AssertEqual(
        true,
        ReferenceEquals(callback, mergedNormalManual.ManualEvaluationCallback),
        "A later normal manual status capture discarded the same order callback.");

    var mergedNormalStandard = normalMerge.Invoke(
        null,
        new object?[]
        {
            normalManual with { CaptureSource = "OrderAdd", ManualOrder = false, ManualEvaluationCallback = null },
            normalManual,
        }) as CapturedRuntimeNormalOrder
        ?? throw new InvalidOperationException("The merged normal standard order had an unexpected type.");
    AssertEqual(false, mergedNormalStandard.ManualOrder, "A reused normal-order slot retained stale ManualOrder=true.");
    AssertEqual<object?>(null, mergedNormalStandard.ManualEvaluationCallback, "A reused normal-order slot retained a stale callback.");
}

static System.Collections.IList GetCaptureOrders(Type captureType)
{
    var ordersField = captureType.GetField("Orders", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{captureType.Name}.Orders was not found.");
    return ordersField.GetValue(null) as System.Collections.IList
        ?? throw new InvalidOperationException($"{captureType.Name}.Orders was not a list.");
}

static MethodInfo GetManualSetter(Type captureType)
{
    return captureType.GetMethod(
        "OnManualControllerOrderSet",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{captureType.Name}.OnManualControllerOrderSet was not found.");
}

static void VerifyOrdinaryOrderIsNotAParseFailure()
{
    var parseOrder = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "ParseOrder",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.ParseOrder was not found.");
    var parseFailures = typeof(SpecialOrderRuntimeCapture).GetField(
        "_parseFailures",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture._parseFailures was not found.");
    var notApplicable = typeof(SpecialOrderRuntimeCapture).GetField(
        "_notApplicableCallbacks",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture._notApplicableCallbacks was not found.");
    var failuresBefore = (int)(parseFailures.GetValue(null) ?? -1);
    var notApplicableBefore = (int)(notApplicable.GetValue(null) ?? -1);

    var parsed = parseOrder.Invoke(null, new object?[] { new OrdinaryOrder(), "Smoke normal", null });
    var parsedManual = parseOrder.Invoke(
        null,
        new object?[] { new ManualOrdinaryOrder(), "Smoke manual normal", null });

    AssertEqual<object?>(null, parsed, "An ordinary order callback was parsed as a special order.");
    AssertEqual<object?>(null, parsedManual, "A manual ordinary order callback was parsed as a special order.");
    AssertEqual(
        failuresBefore,
        (int)(parseFailures.GetValue(null) ?? -1),
        "An ordinary order callback polluted genuine special-order parse failures.");
    AssertEqual(
        notApplicableBefore + 2,
        (int)(notApplicable.GetValue(null) ?? -1),
        "Ordinary order callbacks were not counted as inapplicable special-order callbacks.");
}

static void VerifySkippedManualSetterDoesNotCapture()
{
    var ordersField = typeof(SpecialOrderRuntimeCapture).GetField(
        "Orders",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.Orders was not found.");
    var orders = ordersField.GetValue(null) as System.Collections.IList
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.Orders was not a list.");
    var postfix = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "OnManualControllerOrderSet",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "SpecialOrderRuntimeCapture.OnManualControllerOrderSet was not found.");

    orders.Clear();
    postfix.Invoke(null, new object[] { new object(), new object(), new object(), false });

    AssertEqual(0, orders.Count, "A skipped native manual setter produced a captured order.");
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

    public SpecialOrder(int foodTagId, int beverageTagId, string text, bool manualOrder = false)
    {
        RequestFoodTag = foodTagId;
        RequestBeverageTag = beverageTagId;
        ManualOrder = manualOrder;
        _text = text;
    }

    public string Type => "Special";
    public int DeskCode => 2;
    public int RequestFoodTag { get; }
    public int RequestBeverageTag { get; }
    public bool ManualOrder { get; }
    public SpecialGuest SpecialGuests { get; } = new();

    public override string ToString() => _text;
}

internal sealed class CapturedNormalOrderFixture
{
    public CapturedNormalOrderFixture(bool manualOrder)
    {
        ManualOrder = manualOrder;
    }

    public string Type => "Normal";
    public int DeskCode => 2;
    public bool ManualOrder { get; }
    public TestSellable RequestFood { get; } = new(17);
    public TestSellable RequestBeverage { get; } = new(21);
    public string Guest => "Test normal guest";
}

internal sealed record TestSellable(int id);

internal sealed class OrdinaryOrder
{
    public string Type => "Normal";
}

internal sealed class ManualOrdinaryOrder
{
    public string Type => "Normal";
    public bool ManualOrder => true;
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
