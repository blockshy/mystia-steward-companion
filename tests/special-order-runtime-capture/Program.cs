using System.Reflection;
using MystiaStewardCompanion.Save;
using NormalOrder = NightScene.GuestManagementUtility.GuestsManager.NormalOrder;
using SpecialOrder = NightScene.GuestManagementUtility.GuestsManager.SpecialOrder;

try
{
    VerifyControllerTextResolvesNegativeBeverageTag();
    VerifyControllerOverrideIsAuthoritative();
    VerifyParsedOverrideMatchesByRawIdentity();
    VerifyRawIdentityRemainsWithoutControllerGetter();
    VerifyMergeKeepsRawIdentityAndDisplayTextIndependent();
    VerifyDifferentRuntimeKeysNeverMerge();
    VerifyRuntimeKeysRequireNativePointers();
    VerifyDismissMatchesEveryProvidedRuntimeIdentityField();
    VerifyFulfilledDeliveryStatusRemainsCaptured();
    VerifyControllerBindingCommitsOnlyAfterNativeSuccess();
    VerifyStatusCallbacksCannotCreateUnboundOrders();
    VerifyCompletionRemovalCommitsOnlyAfterNativeSuccess();
    VerifyCleanupRemovalCommitsOnlyAfterNativeSuccess();
    VerifyRepellRemovalCommitsAfterNativeSuccess();
    VerifyNormalCompletionAndCleanupBoundaries();
    VerifyBusinessReadinessRequiresCompleteHooksBeforeTheGeneration();
    VerifyHistoricalOrderStacksAreNotUsedForLiveness();
    VerifyManualSetterHookUsesExact783Signature();
    VerifyLifecycleHooksUseExact783Signatures();
    VerifyObserverHooksUseExact783Signatures();
    VerifyManualSettersBindExactManualStateAndCallbacks();
    VerifyManualBindingSurvivesTransientStateUpdates();
    VerifyManualBindingRetiresWithCapturedOrder();
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

static void VerifyLifecycleHooksUseExact783Signatures()
{
    var managerType = typeof(NightScene.GuestManagementUtility.GuestsManager);
    foreach (var (methodName, predicateName) in new[]
             {
                 ("RemoveFromOrder", "IsExactOrderBaseMethod"),
                 ("EvaluateOrder", "IsExactEvaluateOrder"),
                 ("EvaulateManualOrder", "IsExactManualEvaluateOrder"),
                 ("CleanOrderInfo", "IsExactControllerOnlyMethod"),
                 ("RepellInternal", "IsExactRepellInternal"),
             })
    {
        var method = managerType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"The exact {methodName} fixture was not found.");
        foreach (var captureType in new[]
                 {
                     typeof(SpecialOrderRuntimeCapture),
                     typeof(NormalOrderRuntimeCapture),
                 })
        {
            var predicate = captureType.GetMethod(
                predicateName,
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"{captureType.Name}.{predicateName} was not found.");
            AssertEqual(
                true,
                predicate.Invoke(null, new object[] { method }) is true,
                $"{captureType.Name} rejected the exact BepInEx 783 {methodName} signature.");
        }
    }


    var invalidRepell = typeof(NightScene.GuestManagementUtility.InvalidGuestsManager).GetMethod(
        "RepellInternal",
        BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("The invalid RepellInternal fixture was not found.");
    foreach (var captureType in new[]
             {
                 typeof(SpecialOrderRuntimeCapture),
                 typeof(NormalOrderRuntimeCapture),
             })
    {
        var predicate = captureType.GetMethod(
            "IsExactRepellInternal",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{captureType.Name}.IsExactRepellInternal was not found.");
        AssertEqual(
            false,
            predicate.Invoke(null, new object[] { invalidRepell }) is true,
            $"{captureType.Name} accepted RepellInternal without an exact out bool parameter.");
    }
}

static void VerifyObserverHooksUseExact783Signatures()
{
    var exactType = typeof(NightScene.PartnerUtility.PartnerManager);
    var invalidType = typeof(NightScene.PartnerUtility.InvalidPartnerManager);
    foreach (var (methodName, predicateName) in new[]
             {
                 ("OnOrderBaseStatusUpdate", "IsExactOrderStatusUpdate"),
                 ("NotifySystemChanged", "IsExactNotifySystemChanged"),
             })
    {
        var exact = exactType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"The exact {methodName} observer fixture was not found.");
        var invalid = invalidType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"The invalid {methodName} observer fixture was not found.");
        var predicate = typeof(SpecialOrderRuntimeCapture).GetMethod(
            predicateName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"SpecialOrderRuntimeCapture.{predicateName} was not found.");
        AssertEqual(
            true,
            predicate.Invoke(null, new object[] { exact }) is true,
            $"SpecialOrderRuntimeCapture rejected the exact BepInEx 783 {methodName} signature.");
        AssertEqual(
            false,
            predicate.Invoke(null, new object[] { invalid }) is true,
            $"SpecialOrderRuntimeCapture accepted an invalid {methodName} overload.");
    }

    var observerField = typeof(SpecialOrderRuntimeCapture).GetField(
        "ObserverPatchKeys",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.ObserverPatchKeys was not found.");
    var observers = observerField.GetValue(null) as string[]
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.ObserverPatchKeys was not a string array.");
    AssertEqual(2, observers.Length, "Optional status observer Hook count changed.");
    AssertEqual(
        1,
        observers.Count(key => key.Contains(".OnOrderBaseStatusUpdate/", StringComparison.Ordinal)),
        "The exact order status observer is not tracked separately.");
    AssertEqual(
        1,
        observers.Count(key => key.Contains(".NotifySystemChanged/", StringComparison.Ordinal)),
        "The exact system-change observer is not tracked separately.");

    var patchedField = typeof(SpecialOrderRuntimeCapture).GetField(
        "PatchedMethods",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.PatchedMethods was not found.");
    var patched = patchedField.GetValue(null) as ISet<string>
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.PatchedMethods was not a string set.");
    var previous = patched.ToArray();
    try
    {
        patched.Clear();
        AssertContains(SpecialOrderRuntimeCapture.Status, "observers=0/2", "Observer readiness did not report the empty optional set.");
        patched.Add(observers[0]);
        AssertContains(SpecialOrderRuntimeCapture.Status, "observers=1/2", "Observer readiness did not report a partial optional set.");
        patched.Add(observers[1]);
        AssertContains(SpecialOrderRuntimeCapture.Status, "observers=2/2", "Observer readiness did not report the complete optional set.");
    }
    finally
    {
        patched.Clear();
        foreach (var key in previous) patched.Add(key);
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

static void VerifyRawIdentityRemainsWithoutControllerGetter()
{
    var runtimeOrder = new SpecialOrder(17, -1, BuildOrderText("甜", "无酒精"));
    var captured = Parse(runtimeOrder, new object());
    AssertEqual("", captured.FoodTagDisplayText, "Order ToString text was used as a food display fallback.");
    AssertEqual("", captured.BeverageTagDisplayText, "Order ToString text was used as a beverage display fallback.");
    AssertEqual(-1, captured.BeverageTagId, "Negative raw beverage Tag ID was not retained without a controller.");
    AssertEqual(true, captured.HasBeverageTagId, "Readable negative beverage Tag ID was marked as missing without a controller.");
    AssertEqual(0, runtimeOrder.ToStringReads, "Special-order capture invoked the runtime order ToString method.");
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

    var normalSlot = typeof(NormalOrderRuntimeCapture).GetMethod(
        "IsSameOrderSlot",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("NormalOrderRuntimeCapture.IsSameOrderSlot was not found.");
    var firstNormal = new CapturedRuntimeNormalOrder(
        "ptr:1", 2, "Test guest", 17, 21, capturedAt, capturedAt, "First");
    var secondNormal = firstNormal with { RuntimeKey = "ptr:2", CaptureSource = "Second" };
    AssertEqual(
        false,
        normalSlot.Invoke(null, new object[] { firstNormal, secondNormal }) is true,
        "Different normal-order native keys were merged by desk/food/beverage fallback.");
    AssertEqual(
        false,
        normalSlot.Invoke(
            null,
            new object[]
            {
                firstNormal with { RuntimeKey = "" },
                secondNormal with { RuntimeKey = "" },
            }) is true,
        "Normal orders without native keys were treated as the same slot.");
    AssertEqual(
        false,
        canMerge.Invoke(
            null,
            new object[]
            {
                first with { RuntimeKey = "" },
                second with { RuntimeKey = "" },
            }) is true,
        "Special orders without native keys were treated as the same slot.");
}

static void VerifyRuntimeKeysRequireNativePointers()
{
    var specialKey = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "GetRuntimeObjectKey",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.GetRuntimeObjectKey was not found.");
    var normalKey = typeof(NormalOrderRuntimeCapture).GetMethod(
        "RuntimeOrderKey",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("NormalOrderRuntimeCapture.RuntimeOrderKey was not found.");
    var specialOrder = new SpecialOrder(17, -1, "");
    var normalOrder = new NormalOrder();

    AssertEqual(
        $"ptr:{specialOrder.Pointer.ToInt64():x}",
        specialKey.Invoke(null, new object?[] { specialOrder }) as string,
        "Special capture did not use the exact native order pointer.");
    AssertEqual(
        $"ptr:{normalOrder.Pointer.ToInt64():x}",
        normalKey.Invoke(null, new object?[] { normalOrder }) as string,
        "Normal capture did not use the exact native order pointer.");
    AssertEqual(
        "",
        specialKey.Invoke(null, new object?[] { new object() }) as string,
        "Special capture replaced a missing native pointer with a managed identity.");
    AssertEqual(
        "",
        normalKey.Invoke(null, new object?[] { new object() }) as string,
        "Normal capture replaced a missing native pointer with a managed identity.");
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
            false, false, capturedAt, capturedAt, "ptr:fulfilled", "Test")
        {
            OrderObject = new object(),
            ControllerObject = new object(),
        };
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

static void VerifyCompletionRemovalCommitsOnlyAfterNativeSuccess()
{
    var orders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var addOrder = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "AddOrder",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.AddOrder was not found.");
    var captureBefore = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "CaptureControllerOrderBeforeCompletion",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The completion prefix was not found.");
    var commitAfter = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "OnOrderCompletionSucceeded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The completion postfix was not found.");
    var nativeMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
        "EvaluateOrder",
        BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("The EvaluateOrder fixture was not found.");
    var runtimeOrder = new SpecialOrder(17, -1, "");
    var controller = new SpecialOrderController("甜", "无酒精") { CurrentOrder = runtimeOrder };

    orders.Clear();
    try
    {
        var captured = Parse(runtimeOrder, controller);
        addOrder.Invoke(null, new object?[] { captured });

        var prefixArguments = new object?[] { controller, nativeMethod, null };
        captureBefore.Invoke(null, prefixArguments);
        var state = prefixArguments[2] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The completion prefix did not latch the current SpecialOrder.");

        commitAfter.Invoke(null, new object?[] { state, nativeMethod, false });
        AssertEqual(1, orders.Count, "A skipped native evaluation removed the captured order.");

        commitAfter.Invoke(null, new object?[] { state, nativeMethod, true });
        AssertEqual(1, orders.Count, "An unfulfilled order was retired merely because native evaluation returned.");

        commitAfter.Invoke(null, new object?[] { state with { IsFulfilled = true }, nativeMethod, true });
        AssertEqual(0, orders.Count, "A successful native evaluation did not retire the fulfilled captured order.");
    }
    finally
    {
        orders.Clear();
    }
}

static void VerifyControllerBindingCommitsOnlyAfterNativeSuccess()
{
    var specialOrders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var specialCallback = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "OnControllerOrderAdded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.OnControllerOrderAdded was not found.");
    var runtimeOrder = new SpecialOrder(17, -1, "");
    var controller = new SpecialOrderController("甜", "无酒精") { CurrentOrder = runtimeOrder };

    specialOrders.Clear();
    try
    {
        specialCallback.Invoke(null, new object?[] { controller, runtimeOrder, false });
        AssertEqual(0, specialOrders.Count, "A skipped native PushToOrder created a special-order binding.");

        specialCallback.Invoke(null, new object?[] { controller, runtimeOrder, true });
        AssertEqual(1, specialOrders.Count, "A successful native PushToOrder did not create its exact special-order binding.");
        var captured = specialOrders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The controller binding had an unexpected type.");
        AssertEqual(true, ReferenceEquals(runtimeOrder, captured.OrderObject), "The binding lost the exact order object.");
        AssertEqual(true, ReferenceEquals(controller, captured.ControllerObject), "The binding lost the exact controller object.");
    }
    finally
    {
        specialOrders.Clear();
    }

    var normalOrders = GetCaptureOrders(typeof(NormalOrderRuntimeCapture));
    var normalCallback = typeof(NormalOrderRuntimeCapture).GetMethod(
        "OnControllerOrderAdded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("NormalOrderRuntimeCapture.OnControllerOrderAdded was not found.");
    var normalOrder = new NormalOrder();
    var normalController = new NightScene.GuestManagementUtility.GuestGroupController();

    normalOrders.Clear();
    try
    {
        normalCallback.Invoke(null, new object?[] { normalController, normalOrder, false });
        AssertEqual(0, normalOrders.Count, "A skipped native PushToOrder created a normal-order binding.");

        normalCallback.Invoke(null, new object?[] { normalController, normalOrder, true });
        AssertEqual(1, normalOrders.Count, "A successful native PushToOrder did not create its exact normal-order binding.");
        var captured = normalOrders[0] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The normal controller binding had an unexpected type.");
        AssertEqual(true, ReferenceEquals(normalOrder, captured.OrderObject), "The normal binding lost the exact order object.");
        AssertEqual(true, ReferenceEquals(normalController, captured.ControllerObject), "The normal binding lost the exact controller object.");
    }
    finally
    {
        normalOrders.Clear();
    }
}

static void VerifyStatusCallbacksCannotCreateUnboundOrders()
{
    var orders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var status = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "OnOrderStatusUpdated",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.OnOrderStatusUpdated was not found.");
    var system = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "OnOrderSystemChanged",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.OnOrderSystemChanged was not found.");
    var order = new SpecialOrder(17, -1, "") { IsFullfilled = true };

    orders.Clear();
    status.Invoke(null, new object?[] { order, "FoodDelivered" });
    system.Invoke(null, new object?[] { new object(), "FoodDelivered", order });
    AssertEqual(0, orders.Count, "A status/UI callback created a special order without a confirmed controller binding.");

    var source = File.ReadAllText(FindRepositoryFile(
        "mods", "bepinex", "src", "Save", "SpecialOrderRuntimeCapture.cs"));
    AssertEqual(
        3,
        System.Text.RegularExpressions.Regex.Matches(source, @"\bAddOrder\(").Count,
        "Special-order cache creation is no longer limited to its definition and two confirmed binding callbacks.");
    AssertEqual(false, source.Contains("EndDlc4SpecialManualOrder", StringComparison.Ordinal),
        "The arrival-event cleanup was restored as an order retirement hook.");
    var ownershipPath = Path.Combine(
        Path.GetDirectoryName(FindRepositoryFile("mods", "bepinex", "src", "Save", "SpecialOrderRuntimeCapture.cs"))!,
        "RuntimeSpecialOrderOwnership.cs");
    AssertEqual(false, File.Exists(ownershipPath), "The deleted historical-stack ownership implementation was restored.");
}

static void VerifyCleanupRemovalCommitsOnlyAfterNativeSuccess()
{
    var orders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var addOrder = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "AddOrder",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.AddOrder was not found.");
    var captureBefore = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "CaptureControllerOrderBeforeCompletion",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The cleanup prefix was not found.");
    var commitAfter = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "OnOrderCleanupSucceeded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The cleanup postfix was not found.");
    var nativeMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
        "CleanOrderInfo",
        BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("The CleanOrderInfo fixture was not found.");
    var runtimeOrder = new SpecialOrder(17, -1, "");
    var controller = new SpecialOrderController("甜", "无酒精") { CurrentOrder = runtimeOrder };

    orders.Clear();
    try
    {
        addOrder.Invoke(null, new object?[] { Parse(runtimeOrder, controller) });
        var prefixArguments = new object?[] { controller, nativeMethod, null };
        captureBefore.Invoke(null, prefixArguments);
        var state = prefixArguments[2] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The cleanup prefix did not latch the current SpecialOrder.");

        commitAfter.Invoke(null, new object?[] { state, nativeMethod, false });
        AssertEqual(1, orders.Count, "A skipped native cleanup removed the captured order.");

        commitAfter.Invoke(null, new object?[] { state, nativeMethod, true });
        AssertEqual(0, orders.Count, "A successful native cleanup did not retire the current order.");
    }
    finally
    {
        orders.Clear();
    }
}

static void VerifyRepellRemovalCommitsAfterNativeSuccess()
{
    var nativeMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
        "RepellInternal",
        BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("The RepellInternal fixture was not found.");

    var specialOrders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var specialAdded = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "OnControllerOrderAdded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.OnControllerOrderAdded was not found.");
    var specialBefore = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "CaptureControllerOrderBeforeRepell",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The special RepellInternal prefix was not found.");
    var specialAfter = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "OnOrderRepellSucceeded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The special RepellInternal postfix was not found.");
    var specialOrder = new SpecialOrder(17, -1, "");
    var specialController = new SpecialOrderController("甜", "无酒精") { CurrentOrder = specialOrder };

    specialOrders.Clear();
    try
    {
        specialAdded.Invoke(null, new object?[] { specialController, specialOrder, true });
        var prefixArguments = new object?[] { specialController, nativeMethod, null };
        specialBefore.Invoke(null, prefixArguments);
        var state = prefixArguments[2] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The special RepellInternal prefix did not latch its current order.");

        specialAfter.Invoke(null, new object?[] { state, nativeMethod, false });
        AssertEqual(1, specialOrders.Count, "A skipped native RepellInternal removed the special order.");
        specialAfter.Invoke(null, new object?[] { state, nativeMethod, true });
        AssertEqual(0, specialOrders.Count, "A successful native RepellInternal did not retire the special order.");
    }
    finally
    {
        specialOrders.Clear();
    }

    var normalOrders = GetCaptureOrders(typeof(NormalOrderRuntimeCapture));
    var normalAdded = typeof(NormalOrderRuntimeCapture).GetMethod(
        "OnControllerOrderAdded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("NormalOrderRuntimeCapture.OnControllerOrderAdded was not found.");
    var normalBefore = typeof(NormalOrderRuntimeCapture).GetMethod(
        "CaptureControllerOrderBeforeRepell",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The normal RepellInternal prefix was not found.");
    var normalAfter = typeof(NormalOrderRuntimeCapture).GetMethod(
        "OnControllerOrderRepellSucceeded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The normal RepellInternal postfix was not found.");
    var normalOrder = new NormalOrder();
    var normalController = new NightScene.GuestManagementUtility.GuestGroupController
    {
        CurrentOrder = normalOrder,
    };

    normalOrders.Clear();
    try
    {
        normalAdded.Invoke(null, new object?[] { normalController, normalOrder, true });
        var prefixArguments = new object?[] { normalController, nativeMethod, null };
        normalBefore.Invoke(null, prefixArguments);
        var state = prefixArguments[2] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The normal RepellInternal prefix did not latch its current order.");

        normalAfter.Invoke(null, new object?[] { state, nativeMethod, false });
        AssertEqual(1, normalOrders.Count, "A skipped native RepellInternal removed the normal order.");
        normalAfter.Invoke(null, new object?[] { state, nativeMethod, true });
        AssertEqual(0, normalOrders.Count, "A successful native RepellInternal did not retire the normal order.");
    }
    finally
    {
        normalOrders.Clear();
    }
}

static void VerifyNormalCompletionAndCleanupBoundaries()
{
    var orders = GetCaptureOrders(typeof(NormalOrderRuntimeCapture));
    var added = typeof(NormalOrderRuntimeCapture).GetMethod(
        "OnControllerOrderAdded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("NormalOrderRuntimeCapture.OnControllerOrderAdded was not found.");
    var completionBefore = typeof(NormalOrderRuntimeCapture).GetMethod(
        "CaptureControllerOrderBeforeCompletion",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The normal completion prefix was not found.");
    var completionAfter = typeof(NormalOrderRuntimeCapture).GetMethod(
        "OnControllerOrderCompletionSucceeded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The normal completion postfix was not found.");
    var cleanupBefore = typeof(NormalOrderRuntimeCapture).GetMethod(
        "CaptureControllerOrderBeforeCleanup",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The normal cleanup prefix was not found.");
    var cleanupAfter = typeof(NormalOrderRuntimeCapture).GetMethod(
        "OnControllerOrderCleanupSucceeded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("The normal cleanup postfix was not found.");
    var evaluateMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
        "EvaluateOrder",
        BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("The EvaluateOrder fixture was not found.");
    var cleanupMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
        "CleanOrderInfo",
        BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("The CleanOrderInfo fixture was not found.");
    var order = new NormalOrder();
    var controller = new NightScene.GuestManagementUtility.GuestGroupController { CurrentOrder = order };

    orders.Clear();
    try
    {
        added.Invoke(null, new object?[] { controller, order, true });
        var completionArguments = new object?[] { controller, evaluateMethod, null };
        completionBefore.Invoke(null, completionArguments);
        var unfulfilled = completionArguments[2] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The normal completion prefix did not latch its current order.");
        completionAfter.Invoke(null, new object?[] { unfulfilled, evaluateMethod, true });
        AssertEqual(1, orders.Count, "A successful normal evaluation retired an unfulfilled order.");

        order.IsFullfilled = true;
        completionArguments = new object?[] { controller, evaluateMethod, null };
        completionBefore.Invoke(null, completionArguments);
        var fulfilled = completionArguments[2] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The normal completion prefix did not reread fulfilled state.");
        completionAfter.Invoke(null, new object?[] { fulfilled, evaluateMethod, false });
        AssertEqual(1, orders.Count, "A skipped normal evaluation retired a fulfilled order.");
        completionAfter.Invoke(null, new object?[] { fulfilled, evaluateMethod, true });
        AssertEqual(0, orders.Count, "A successful normal evaluation did not retire a fulfilled order.");

        var cleanupOrder = new NormalOrder();
        controller.CurrentOrder = cleanupOrder;
        added.Invoke(null, new object?[] { controller, cleanupOrder, true });
        var cleanupArguments = new object?[] { controller, cleanupMethod, null };
        cleanupBefore.Invoke(null, cleanupArguments);
        var cleanupState = cleanupArguments[2] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The normal cleanup prefix did not latch its current order.");
        cleanupAfter.Invoke(null, new object?[] { cleanupState, cleanupMethod, false });
        AssertEqual(1, orders.Count, "A skipped normal cleanup retired its order.");
        cleanupAfter.Invoke(null, new object?[] { cleanupState, cleanupMethod, true });
        AssertEqual(0, orders.Count, "A successful normal cleanup did not retire its order.");
    }
    finally
    {
        orders.Clear();
    }
}

static void VerifyBusinessReadinessRequiresCompleteHooksBeforeTheGeneration()
{
    var wasActive = RuntimeNightBusinessLifecycle.IsActive;
    var previousGeneration = RuntimeNightBusinessLifecycle.Generation;
    try
    {
        foreach (var captureType in new[]
                 {
                     typeof(SpecialOrderRuntimeCapture),
                     typeof(NormalOrderRuntimeCapture),
                 })
        {
            var patchedField = captureType.GetField("PatchedMethods", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"{captureType.Name}.PatchedMethods was not found.");
            var requiredField = captureType.GetField("RequiredPatchKeys", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"{captureType.Name}.RequiredPatchKeys was not found.");
            var firstCoveredField = captureType.GetField(
                "_firstCoveredBusinessGeneration",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"{captureType.Name}._firstCoveredBusinessGeneration was not found.");
            var patched = patchedField.GetValue(null) as ISet<string>
                ?? throw new InvalidOperationException($"{captureType.Name}.PatchedMethods was not a string set.");
            var required = requiredField.GetValue(null) as string[]
                ?? throw new InvalidOperationException($"{captureType.Name}.RequiredPatchKeys was not a string array.");
            var requiredMethodNames = new[]
            {
                "PushToOrder",
                "SetManualControllerOrderInternal",
                "RemoveFromOrder",
                "EvaluateOrder",
                "EvaulateManualOrder",
                "CleanOrderInfo",
                "RepellInternal",
            };
            AssertEqual(7, required.Length, $"{captureType.Name} changed the complete required lifecycle Hook count.");
            foreach (var methodName in requiredMethodNames)
            {
                AssertEqual(
                    1,
                    required.Count(key => key.Contains($".{methodName}/", StringComparison.Ordinal)),
                    $"{captureType.Name} does not require exactly one {methodName} Hook.");
            }
            var previousPatched = patched.ToArray();
            var previousFirstCovered = (long)(firstCoveredField.GetValue(null) ?? long.MaxValue);

            try
            {
                RuntimeNightBusinessLifecycle.IsActive = true;
                RuntimeNightBusinessLifecycle.Generation = 7;
                patched.Clear();
                firstCoveredField.SetValue(null, 8L);
                foreach (var key in required.Take(required.Length - 1)) patched.Add(key);
                AssertEqual(false, ReadBusinessReady(captureType), $"{captureType.Name} accepted an incomplete required Hook set.");

                patched.Add(required[^1]);
                AssertEqual(false, ReadBusinessReady(captureType), $"{captureType.Name} accepted Hooks installed after generation 7 began.");
                RuntimeNightBusinessLifecycle.Generation = 8;
                AssertEqual(true, ReadBusinessReady(captureType), $"{captureType.Name} did not open on the first fully covered generation.");

                RuntimeNightBusinessLifecycle.IsActive = false;
                RuntimeNightBusinessLifecycle.Generation = 7;
                AssertEqual(true, ReadBusinessReady(captureType), $"{captureType.Name} incorrectly applied the active-generation gate outside business.");

                var source = File.ReadAllText(FindRepositoryFile(
                    "mods", "bepinex", "src", "Save", $"{captureType.Name}.cs"));
                AssertContains(
                    source,
                    "checked(RuntimeNightBusinessLifecycle.Generation + 1)",
                    $"{captureType.Name} no longer defers late Hook completion to the next business generation.");
            }
            finally
            {
                patched.Clear();
                foreach (var key in previousPatched) patched.Add(key);
                firstCoveredField.SetValue(null, previousFirstCovered);
            }
        }
    }
    finally
    {
        RuntimeNightBusinessLifecycle.IsActive = wasActive;
        RuntimeNightBusinessLifecycle.Generation = previousGeneration;
    }
}

static bool ReadBusinessReady(Type captureType)
{
    var property = captureType.GetProperty("IsBusinessReady", BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{captureType.Name}.IsBusinessReady was not found.");
    return property.GetValue(null) is true;
}

static void VerifyHistoricalOrderStacksAreNotUsedForLiveness()
{
    var provider = File.ReadAllText(FindRepositoryFile(
        "mods", "bepinex", "src", "Save", "NightBusinessReflectionProvider.cs"));
    var matching = File.ReadAllText(FindRepositoryFile(
        "mods", "bepinex", "src", "Save", "RuntimeOrderPreparationService.OrderMatching.cs"));
    var normalSnapshot = File.ReadAllText(FindRepositoryFile(
        "mods", "bepinex", "src", "Save", "RuntimeNormalOrderSnapshotService.cs"));
    var capture = File.ReadAllText(FindRepositoryFile(
        "mods", "bepinex", "src", "Save", "SpecialOrderRuntimeCapture.cs"));

    AssertEqual(false, provider.Contains("\"AllOrders\"", StringComparison.Ordinal),
        "Night-business projection restored the historical AllOrders stack.");
    AssertEqual(false, provider.Contains("\"AllOrdersData\"", StringComparison.Ordinal),
        "Night-business projection restored the historical AllOrdersData stack.");
    AssertEqual(false, matching.Contains("\"AllOrders\"", StringComparison.Ordinal),
        "Automation liveness restored the historical AllOrders stack.");
    AssertEqual(false, matching.Contains("\"AllOrdersData\"", StringComparison.Ordinal),
        "Automation liveness restored the historical AllOrdersData stack.");
    AssertEqual(false, normalSnapshot.Contains("\"AllOrders\"", StringComparison.Ordinal),
        "Normal-order projection restored the historical AllOrders stack.");
    AssertEqual(false, normalSnapshot.Contains("\"AllOrdersData\"", StringComparison.Ordinal),
        "Normal-order projection restored the historical AllOrdersData stack.");
    AssertContains(capture, "\"PushToOrder\"", "The exact order/controller binding hook is missing.");
    AssertContains(capture, "nameof(OnControllerOrderAdded)", "PushToOrder no longer commits through its postfix.");
    AssertContains(capture, "\"CleanOrderInfo\"", "The direct native cleanup boundary is missing.");
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
        AssertEqual(true, capturedSpecial.ManualEvaluationBindingObserved, "The special manual setter binding was not recorded.");
        AssertEqual(false, capturedSpecial.ManualEvaluationBindingConflict, "The first special manual setter binding was marked conflicting.");
        AssertEqual(
            true,
            ReferenceEquals(specialCallback, capturedSpecial.ManualEvaluationBindingCallback),
            "The stable special manual binding did not retain the original setter callback.");

        normalSetter.Invoke(
            null,
            new object?[]
            {
                new object(),
                normalCallback,
                new NormalOrder(manualOrder: true),
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
        AssertEqual(true, capturedNormal.ManualEvaluationBindingObserved, "The normal manual setter binding was not recorded.");
        AssertEqual(false, capturedNormal.ManualEvaluationBindingConflict, "The first normal manual setter binding was marked conflicting.");
        AssertEqual(
            true,
            ReferenceEquals(normalCallback, capturedNormal.ManualEvaluationBindingCallback),
            "The stable normal manual binding did not retain the original setter callback.");

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
        AssertEqual(true, missingCallback.ManualEvaluationBindingObserved, "A special setter with a missing callback was hidden.");
        AssertEqual<object?>(null, missingCallback.ManualEvaluationBindingCallback, "A missing special binding callback was replaced.");

        normalOrders.Clear();
        normalSetter.Invoke(
            null,
            new object?[]
            {
                new object(),
                null,
                new NormalOrder(manualOrder: true),
                true,
            });
        var missingNormalCallback = normalOrders[0] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The incomplete normal manual capture had an unexpected type.");
        AssertEqual(true, missingNormalCallback.ManualOrder, "A missing callback erased the normal manual-order identity.");
        AssertEqual<object?>(null, missingNormalCallback.ManualEvaluationCallback, "A missing normal callback was replaced by a fallback object.");
        AssertEqual(true, missingNormalCallback.ManualEvaluationBindingObserved, "A normal setter with a missing callback was hidden.");
        AssertEqual<object?>(null, missingNormalCallback.ManualEvaluationBindingCallback, "A missing normal binding callback was replaced.");
    }
    finally
    {
        specialOrders.Clear();
        normalOrders.Clear();
    }
}

static void VerifyManualBindingSurvivesTransientStateUpdates()
{
    var capturedAt = DateTime.UtcNow;
    var callback = new object();
    var conflictingCallback = new object();
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
        ManualEvaluationBindingObserved = true,
        ManualEvaluationBindingCallback = callback,
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

    var mergedSpecialTransientFalse = specialMerge.Invoke(
        null,
        new object?[]
        {
            specialManual with
            {
                CaptureSource = "OrderAdd",
                ManualOrder = false,
                ManualEvaluationCallback = null,
                ManualEvaluationBindingObserved = false,
                ManualEvaluationBindingCallback = null,
            },
            specialManual,
        }) as CapturedRuntimeSpecialOrder
        ?? throw new InvalidOperationException("The merged special transient-state order had an unexpected type.");
    AssertEqual(false, mergedSpecialTransientFalse.ManualOrder, "The current special ManualOrder=false state was not retained.");
    AssertEqual<object?>(null, mergedSpecialTransientFalse.ManualEvaluationCallback, "The current special callback state was not cleared.");
    AssertEqual(true, mergedSpecialTransientFalse.ManualEvaluationBindingObserved, "A transient special status update erased the setter binding.");
    AssertEqual(false, mergedSpecialTransientFalse.ManualEvaluationBindingConflict, "A transient special status update created a false binding conflict.");
    AssertEqual(
        true,
        ReferenceEquals(callback, mergedSpecialTransientFalse.ManualEvaluationBindingCallback),
        "A transient special status update erased the stable setter callback.");

    var conflictingSpecial = specialMerge.Invoke(
        null,
        new object?[]
        {
            specialManual with
            {
                CaptureSource = "ManualOrderSet",
                ManualEvaluationCallback = conflictingCallback,
                ManualEvaluationBindingCallback = conflictingCallback,
            },
            specialManual,
        }) as CapturedRuntimeSpecialOrder
        ?? throw new InvalidOperationException("The conflicting special binding had an unexpected type.");
    AssertEqual(true, conflictingSpecial.ManualEvaluationBindingConflict, "Different special setter callbacks were not rejected.");

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
        ManualEvaluationBindingObserved = true,
        ManualEvaluationBindingCallback = callback,
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

    var mergedNormalTransientFalse = normalMerge.Invoke(
        null,
        new object?[]
        {
            normalManual with
            {
                CaptureSource = "OrderAdd",
                ManualOrder = false,
                ManualEvaluationCallback = null,
                ManualEvaluationBindingObserved = false,
                ManualEvaluationBindingCallback = null,
            },
            normalManual,
        }) as CapturedRuntimeNormalOrder
        ?? throw new InvalidOperationException("The merged normal transient-state order had an unexpected type.");
    AssertEqual(false, mergedNormalTransientFalse.ManualOrder, "The current normal ManualOrder=false state was not retained.");
    AssertEqual<object?>(null, mergedNormalTransientFalse.ManualEvaluationCallback, "The current normal callback state was not cleared.");
    AssertEqual(true, mergedNormalTransientFalse.ManualEvaluationBindingObserved, "A transient normal status update erased the setter binding.");
    AssertEqual(false, mergedNormalTransientFalse.ManualEvaluationBindingConflict, "A transient normal status update created a false binding conflict.");
    AssertEqual(
        true,
        ReferenceEquals(callback, mergedNormalTransientFalse.ManualEvaluationBindingCallback),
        "A transient normal status update erased the stable setter callback.");

    var conflictingNormal = normalMerge.Invoke(
        null,
        new object?[]
        {
            normalManual with
            {
                CaptureSource = "ManualOrderSet",
                ManualEvaluationCallback = conflictingCallback,
                ManualEvaluationBindingCallback = conflictingCallback,
            },
            normalManual,
        }) as CapturedRuntimeNormalOrder
        ?? throw new InvalidOperationException("The conflicting normal binding had an unexpected type.");
    AssertEqual(true, conflictingNormal.ManualEvaluationBindingConflict, "Different normal setter callbacks were not rejected.");

    var reusedNormalSlot = normalMerge.Invoke(
        null,
        new object?[]
        {
            normalManual with
            {
                FoodId = 99,
                CaptureSource = "OrderAdd",
                ManualOrder = false,
                ManualEvaluationCallback = null,
                ManualEvaluationBindingObserved = false,
                ManualEvaluationBindingCallback = null,
            },
            normalManual,
        }) as CapturedRuntimeNormalOrder
        ?? throw new InvalidOperationException("The reused normal slot had an unexpected type.");
    AssertEqual(false, reusedNormalSlot.ManualEvaluationBindingObserved, "A distinct normal order inherited a retired setter binding.");
    AssertEqual<object?>(null, reusedNormalSlot.ManualEvaluationBindingCallback, "A distinct normal order inherited a retired callback.");
}

static void VerifyManualBindingRetiresWithCapturedOrder()
{
    var capturedAt = DateTime.UtcNow;
    var callback = new object();
    var specialOrders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var normalOrders = GetCaptureOrders(typeof(NormalOrderRuntimeCapture));
    var specialAdd = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "AddOrder");
    var specialRemove = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "RemoveOrder");
    var normalAdd = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "AddOrder");
    var normalRemove = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "RemoveOrder");
    var orderObject = new object();
    var controllerObject = new object();

    var specialManual = new CapturedRuntimeSpecialOrder(
        2, 1003, "Yuuma", 30, true, "灼热", -1, true, "无酒精",
        false, false, capturedAt, capturedAt, "ptr:same", "ManualOrderSet")
    {
        OrderObject = orderObject,
        ControllerObject = controllerObject,
        ManualOrder = true,
        ManualEvaluationCallback = callback,
        ManualEvaluationBindingObserved = true,
        ManualEvaluationBindingCallback = callback,
    };
    var normalManual = new CapturedRuntimeNormalOrder(
        "ptr:same", 2, "Yuuma", 17, 3, capturedAt, capturedAt, "ManualOrderSet")
    {
        OrderObject = orderObject,
        ControllerObject = controllerObject,
        ManualOrder = true,
        ManualEvaluationCallback = callback,
        ManualEvaluationBindingObserved = true,
        ManualEvaluationBindingCallback = callback,
    };

    specialOrders.Clear();
    normalOrders.Clear();
    try
    {
        specialAdd.Invoke(null, new object?[] { specialManual });
        specialRemove.Invoke(null, new object?[] { specialManual });
        specialAdd.Invoke(null, new object?[]
        {
            specialManual with
            {
                CaptureSource = "OrderAdd",
                ManualOrder = false,
                ManualEvaluationCallback = null,
                ManualEvaluationBindingObserved = false,
                ManualEvaluationBindingCallback = null,
            },
        });
        var specialReused = specialOrders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The reused special capture had an unexpected type.");
        AssertEqual(false, specialReused.ManualEvaluationBindingObserved, "A retired special binding survived exact pointer and identity reuse.");
        AssertEqual<object?>(null, specialReused.ManualEvaluationBindingCallback, "A retired special callback survived exact pointer and identity reuse.");

        normalAdd.Invoke(null, new object?[] { normalManual });
        normalRemove.Invoke(null, new object?[] { normalManual });
        normalAdd.Invoke(null, new object?[]
        {
            normalManual with
            {
                CaptureSource = "OrderAdd",
                ManualOrder = false,
                ManualEvaluationCallback = null,
                ManualEvaluationBindingObserved = false,
                ManualEvaluationBindingCallback = null,
            },
        });
        var normalReused = normalOrders[0] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The reused normal capture had an unexpected type.");
        AssertEqual(false, normalReused.ManualEvaluationBindingObserved, "A retired normal binding survived exact pointer and identity reuse.");
        AssertEqual<object?>(null, normalReused.ManualEvaluationBindingCallback, "A retired normal callback survived exact pointer and identity reuse.");
    }
    finally
    {
        specialOrders.Clear();
        normalOrders.Clear();
    }
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

static MethodInfo GetCaptureMutation(Type captureType, string methodName)
{
    return captureType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{captureType.Name}.{methodName} was not found.");
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

static string FindRepositoryFile(params string[] segments)
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
    {
        var path = segments.Aggregate(directory.FullName, (current, segment) => Path.Combine(current, segment));
        if (File.Exists(path)) return path;
    }

    throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
}

static void AssertContains(string actual, string expected, string message)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Missing '{expected}'.");
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

internal sealed class OrdinaryOrder
{
    public string Type => "Normal";
}

internal sealed class ManualOrdinaryOrder
{
    public string Type => "Normal";
    public bool ManualOrder => true;
}

internal sealed class SpecialOrderController : NightScene.GuestManagementUtility.GuestGroupController
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
