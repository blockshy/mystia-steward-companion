using System.Reflection;
using MystiaStewardCompanion.Save;
using NormalOrder = NightScene.GuestManagementUtility.GuestsManager.NormalOrder;
using SpecialOrder = NightScene.GuestManagementUtility.GuestsManager.SpecialOrder;

try
{
    VerifyRawSignedTagsAvoidTextGetters();
    VerifyRawIdentityMatchesWithoutDisplayText();
    VerifyMissingRawTagFailsClosed();
    VerifyProductionSourcesRejectTextGetterPaths();
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
    VerifyTerminalReceiptsUseExactHookIdentity();
    VerifyCapturedOrderLifecycleAbaKeepsTheNewBinding();
    VerifyBoundStatusObserversDoNotAdvanceLifecycle();
    VerifyRawTagDriftQuarantinesTheLifecycle();
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
    Console.WriteLine("PASS: special-order capture uses exact raw signed Tag identity without text getters or text fallback.");
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

static void VerifyRawSignedTagsAvoidTextGetters()
{
    var controller = new SpecialOrderController("unused food text", "unused beverage text")
    {
        ThrowOnTextRead = true,
    };
    var runtimeOrder = new SpecialOrder(17, -1, BuildOrderText("stale food", "stale beverage"));
    var captured = Parse(runtimeOrder, controller);

    AssertEqual(17, captured.FoodTagId, "Food Tag ID changed unexpectedly.");
    AssertEqual(-1, captured.BeverageTagId, "Negative beverage Tag ID changed unexpectedly.");
    AssertEqual(0, controller.FoodReads, "Special-order capture invoked GetOrderFoodText.");
    AssertEqual(0, controller.BeverageReads, "Special-order capture invoked GetOrderBevText.");
    AssertEqual(0, runtimeOrder.ToStringReads, "Special-order capture invoked OrderBase.ToString.");

    var zeroAndNegative = Parse(new SpecialOrder(0, -1, ""), new object());
    AssertEqual(0, zeroAndNegative.FoodTagId, "A readable zero food Tag ID was treated as missing.");
    AssertEqual(-1, zeroAndNegative.BeverageTagId, "A readable negative beverage Tag ID was treated as missing.");
}

static void VerifyRawIdentityMatchesWithoutDisplayText()
{
    var captured = Parse(new SpecialOrder(30, 14, BuildOrderText("stale food", "stale beverage")), new object());
    var matched = RareOrderIdentityMatcher.Matches(
        new RareOrderIdentity(2, 123, 30, 14),
        new RareOrderIdentity(
            captured.DeskCode,
            captured.GuestId,
            captured.FoodTagId,
            captured.BeverageTagId),
        out var reason);
    AssertEqual(true, matched, $"Raw signed Tag identity did not match exactly: {reason}");
}

static void VerifyMissingRawTagFailsClosed()
{
    AssertEqual<object?>(
        null,
        TryParse(new SpecialOrder(null, -1, BuildOrderText("fallback food", "fallback beverage")), new SpecialOrderController("fallback", "fallback")),
        "A SpecialOrder missing RequestFoodTag was accepted through text fallback.");
    AssertEqual<object?>(
        null,
        TryParse(new SpecialOrder(17, null, BuildOrderText("fallback food", "fallback beverage")), new SpecialOrderController("fallback", "fallback")),
        "A SpecialOrder missing RequestBeverageTag was accepted through text fallback.");
}

static void VerifyProductionSourcesRejectTextGetterPaths()
{
    var captureSource = File.ReadAllText(FindRepositoryFile(
        "mods", "bepinex", "src", "Save", "SpecialOrderRuntimeCapture.cs"));
    var providerSource = File.ReadAllText(FindRepositoryFile(
        "mods", "bepinex", "src", "Save", "NightBusinessReflectionProvider.cs"));
    foreach (var source in new[] { captureSource, providerSource })
    {
        AssertEqual(false, source.Contains("GetOrderFoodText", StringComparison.Ordinal),
            "Production code restored the SpecialOrder food-text getter.");
        AssertEqual(false, source.Contains("GetOrderBevText", StringComparison.Ordinal),
            "Production code restored the SpecialOrder beverage-text getter.");
        AssertEqual(false, source.Contains("FoodTagDisplayText", StringComparison.Ordinal),
            "Production code restored captured food display-text state.");
        AssertEqual(false, source.Contains("BeverageTagDisplayText", StringComparison.Ordinal),
            "Production code restored captured beverage display-text state.");
        AssertEqual(false, source.Contains("HasFoodTagId", StringComparison.Ordinal),
            "Production code restored partial food Tag identity compatibility.");
        AssertEqual(false, source.Contains("HasBeverageTagId", StringComparison.Ordinal),
            "Production code restored partial beverage Tag identity compatibility.");
        AssertEqual(false, source.Contains("MergeTagParts", StringComparison.Ordinal),
            "Production code restored partial Tag identity merging.");
    }
}

static void VerifyDifferentRuntimeKeysNeverMerge()
{
    var capturedAt = DateTime.UtcNow;
    var first = new CapturedRuntimeSpecialOrder(
        2, 123, "Test guest", 30, -1,
        false, false, capturedAt, capturedAt, "ptr:1", "First");
    var second = first with { RuntimeKey = "ptr:2", CaptureSource = "Second" };
    var canMerge = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "CanMergeCapturedOrders",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.CanMergeCapturedOrders was not found.");
    var merged = canMerge.Invoke(null, new object[] { first, second }) as bool?;
    AssertEqual(false, merged, "Different native runtime keys were merged by desk/guest fallback.");

    AssertEqual<MethodInfo?>(
        null,
        typeof(SpecialOrderRuntimeCapture).GetMethod(
            "RemoveOrder",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(CapturedRuntimeSpecialOrder) },
            modifiers: null),
        "The obsolete captured-special-order removal overload was retained.");
    AssertEqual<MethodInfo?>(
        null,
        typeof(NormalOrderRuntimeCapture).GetMethod(
            "RemoveOrder",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(CapturedRuntimeNormalOrder) },
            modifiers: null),
        "The obsolete captured-normal-order removal overload was retained.");
    foreach (var captureType in new[]
             {
                 typeof(SpecialOrderRuntimeCapture),
                 typeof(NormalOrderRuntimeCapture),
             })
    {
        AssertEqual<MethodInfo?>(
            null,
            captureType.GetMethod(
                "ParseControllerCurrentOrder",
                BindingFlags.NonPublic | BindingFlags.Static),
            $"{captureType.Name} retained the obsolete controller-to-capture fallback.");
    }

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
        2, 123, "Test guest", 30, -1,
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
            2, 123, "Test guest", 30, -1,
            false, false, capturedAt, capturedAt, "ptr:fulfilled", "Test")
        {
            OrderObject = new object(),
            ControllerObject = new object(),
            OrderLifecycleSequence = 1,
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
    RuntimeOrderTerminalReceiptStore.Clear();
    var orders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var addOrder = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "OnControllerOrderAdded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.OnControllerOrderAdded was not found.");
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
        addOrder.Invoke(null, new object?[] { controller, runtimeOrder, true });
        var receiptLifecycle = RequireActiveLifecycle(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Special,
            runtimeOrder.Pointer,
            controller.Pointer,
            "The successful PushToOrder capture did not begin an order lifecycle.");
        var receiptToken = new RuntimeOrderBindingToken(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Special,
            runtimeOrder.Pointer,
            controller.Pointer,
            receiptLifecycle);

        var prefixArguments = new object?[] { controller, nativeMethod, null };
        captureBefore.Invoke(null, prefixArguments);
        var state = RequireTerminalState(
            prefixArguments[2],
            "The completion prefix did not latch the current SpecialOrder.");

        commitAfter.Invoke(null, new object?[] { state, nativeMethod, false });
        AssertEqual(1, orders.Count, "A skipped native evaluation removed the captured order.");
        AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(receiptToken, out _),
            "A skipped native evaluation published a terminal receipt.");

        commitAfter.Invoke(null, new object?[] { state, nativeMethod, true });
        AssertEqual(1, orders.Count, "An unfulfilled order was retired merely because native evaluation returned.");
        AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(receiptToken, out _),
            "An unfulfilled native evaluation published a successful terminal receipt.");

        runtimeOrder.IsFullfilled = true;
        controller.HasEvaluated = true;
        prefixArguments = new object?[] { controller, nativeMethod, null };
        captureBefore.Invoke(null, prefixArguments);
        AssertEqual<object?>(null, prefixArguments[2],
            "An already-evaluated controller latched a false successful evaluation state.");
        AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(receiptToken, out _),
            "An already-evaluated native no-op published a successful terminal receipt.");

        controller.HasEvaluated = false;
        prefixArguments = new object?[] { controller, nativeMethod, null };
        captureBefore.Invoke(null, prefixArguments);
        var fulfilledState = RequireTerminalState(
            prefixArguments[2],
            "The completion prefix did not reread the fulfilled SpecialOrder.");
        commitAfter.Invoke(null, new object?[] { fulfilledState, nativeMethod, true });
        AssertEqual(0, orders.Count, "A successful native evaluation did not retire the fulfilled captured order.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.TryFind(receiptToken, out var receipt),
            "A fulfilled native evaluation did not publish its exact terminal receipt.");
        AssertEqual(RuntimeOrderTerminalDisposition.Evaluated, receipt.Disposition,
            "A fulfilled native evaluation was not recorded as evaluated.");
    }
    finally
    {
        orders.Clear();
        RuntimeOrderTerminalReceiptStore.Clear();
    }
}

static void VerifyControllerBindingCommitsOnlyAfterNativeSuccess()
{
    RuntimeOrderTerminalReceiptStore.Clear();
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
        AssertNoActiveLifecycle(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Special,
            runtimeOrder.Pointer,
            controller.Pointer,
            "A skipped native PushToOrder started a special-order lifecycle.");

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
        RuntimeOrderTerminalReceiptStore.Clear();
    }

    var normalOrders = GetCaptureOrders(typeof(NormalOrderRuntimeCapture));
    var normalCallback = typeof(NormalOrderRuntimeCapture).GetMethod(
        "OnControllerOrderAdded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("NormalOrderRuntimeCapture.OnControllerOrderAdded was not found.");
    var normalOrder = new NormalOrder();
    var normalController = new NightScene.GuestManagementUtility.GuestGroupController
    {
        CurrentOrder = normalOrder,
    };

    normalOrders.Clear();
    try
    {
        normalCallback.Invoke(null, new object?[] { normalController, normalOrder, false });
        AssertEqual(0, normalOrders.Count, "A skipped native PushToOrder created a normal-order binding.");
        AssertNoActiveLifecycle(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Normal,
            normalOrder.Pointer,
            normalController.Pointer,
            "A skipped native PushToOrder started a normal-order lifecycle.");

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
        RuntimeOrderTerminalReceiptStore.Clear();
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
        "OnControllerOrderAdded",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.OnControllerOrderAdded was not found.");
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
        addOrder.Invoke(null, new object?[] { controller, runtimeOrder, true });
        var prefixArguments = new object?[] { controller, nativeMethod, null };
        captureBefore.Invoke(null, prefixArguments);
        var state = RequireTerminalState(
            prefixArguments[2],
            "The cleanup prefix did not latch the current SpecialOrder.");

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
    RuntimeOrderTerminalReceiptStore.Clear();
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
        var receiptLifecycle = RequireActiveLifecycle(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Special,
            specialOrder.Pointer,
            specialController.Pointer,
            "The special RepellInternal fixture has no active order lifecycle.");
        var prefixArguments = new object?[] { specialController, nativeMethod, null };
        specialBefore.Invoke(null, prefixArguments);
        var state = RequireTerminalState(
            prefixArguments[2],
            "The special RepellInternal prefix did not latch its current order.");

        specialAfter.Invoke(null, new object?[] { state, nativeMethod, false });
        AssertEqual(1, specialOrders.Count, "A skipped native RepellInternal removed the special order.");
        var receiptToken = new RuntimeOrderBindingToken(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Special,
            specialOrder.Pointer,
            specialController.Pointer,
            receiptLifecycle);
        AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(receiptToken, out _),
            "A skipped native RepellInternal published a terminal receipt.");
        specialAfter.Invoke(null, new object?[] { state, nativeMethod, true });
        AssertEqual(0, specialOrders.Count, "A successful native RepellInternal did not retire the special order.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.TryFind(receiptToken, out var receipt),
            "A successful special RepellInternal did not publish a terminal receipt.");
        AssertEqual(RuntimeOrderTerminalReceiptSource.RepellInternal, receipt.Source,
            "The special RepellInternal receipt lost its exact Hook source.");
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
        var receiptLifecycle = RequireActiveLifecycle(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Normal,
            normalOrder.Pointer,
            normalController.Pointer,
            "The normal RepellInternal fixture has no active order lifecycle.");
        var prefixArguments = new object?[] { normalController, nativeMethod, null };
        normalBefore.Invoke(null, prefixArguments);
        var state = RequireTerminalState(
            prefixArguments[2],
            "The normal RepellInternal prefix did not latch its current order.");

        normalAfter.Invoke(null, new object?[] { state, nativeMethod, false });
        AssertEqual(1, normalOrders.Count, "A skipped native RepellInternal removed the normal order.");
        var receiptToken = new RuntimeOrderBindingToken(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Normal,
            normalOrder.Pointer,
            normalController.Pointer,
            receiptLifecycle);
        AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(receiptToken, out _),
            "A skipped native normal RepellInternal published a terminal receipt.");
        normalAfter.Invoke(null, new object?[] { state, nativeMethod, true });
        AssertEqual(0, normalOrders.Count, "A successful native RepellInternal did not retire the normal order.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.TryFind(receiptToken, out var receipt),
            "A successful normal RepellInternal did not publish a terminal receipt.");
        AssertEqual(RuntimeOrderTerminalReceiptSource.RepellInternal, receipt.Source,
            "The normal RepellInternal receipt lost its exact Hook source.");
    }
    finally
    {
        normalOrders.Clear();
        RuntimeOrderTerminalReceiptStore.Clear();
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
        var unfulfilled = RequireTerminalState(
            completionArguments[2],
            "The normal completion prefix did not latch its current order.");
        completionAfter.Invoke(null, new object?[] { unfulfilled, evaluateMethod, true });
        AssertEqual(1, orders.Count, "A successful normal evaluation retired an unfulfilled order.");

        order.IsFullfilled = true;
        completionArguments = new object?[] { controller, evaluateMethod, null };
        completionBefore.Invoke(null, completionArguments);
        var fulfilled = RequireTerminalState(
            completionArguments[2],
            "The normal completion prefix did not reread fulfilled state.");
        completionAfter.Invoke(null, new object?[] { fulfilled, evaluateMethod, false });
        AssertEqual(1, orders.Count, "A skipped normal evaluation retired a fulfilled order.");
        completionAfter.Invoke(null, new object?[] { fulfilled, evaluateMethod, true });
        AssertEqual(0, orders.Count, "A successful normal evaluation did not retire a fulfilled order.");

        var cleanupOrder = new NormalOrder();
        controller.CurrentOrder = cleanupOrder;
        added.Invoke(null, new object?[] { controller, cleanupOrder, true });
        var cleanupArguments = new object?[] { controller, cleanupMethod, null };
        cleanupBefore.Invoke(null, cleanupArguments);
        var cleanupState = RequireTerminalState(
            cleanupArguments[2],
            "The normal cleanup prefix did not latch its current order.");
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

static void VerifyTerminalReceiptsUseExactHookIdentity()
{
    var wasActive = RuntimeNightBusinessLifecycle.IsActive;
    var previousGeneration = RuntimeNightBusinessLifecycle.Generation;
    var specialOrders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var normalOrders = GetCaptureOrders(typeof(NormalOrderRuntimeCapture));
    RuntimeOrderTerminalReceiptStore.Clear();
    specialOrders.Clear();
    normalOrders.Clear();
    try
    {
        RuntimeNightBusinessLifecycle.IsActive = true;
        RuntimeNightBusinessLifecycle.Generation = 73;

        var evaluateMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
            "EvaluateOrder",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The EvaluateOrder fixture was not found.");
        var specialOrder = new SpecialOrder(17, -1, "") { IsFullfilled = true };
        var specialController = new SpecialOrderController("甜", "无酒精")
        {
            CurrentOrder = specialOrder,
        };
        var addSpecial = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnControllerOrderAdded");
        var beforeSpecial = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "CaptureControllerOrderBeforeCompletion");
        var afterSpecial = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnOrderCompletionSucceeded");
        addSpecial.Invoke(null, new object?[] { specialController, specialOrder, true });
        var specialLifecycle = RequireActiveLifecycle(
            73,
            RuntimeOrderKind.Special,
            specialOrder.Pointer,
            specialController.Pointer,
            "The special evaluation fixture has no active order lifecycle.");
        var specialArguments = new object?[] { specialController, evaluateMethod, null };
        beforeSpecial.Invoke(null, specialArguments);
        var specialState = RequireTerminalState(
            specialArguments[2],
            "The special evaluation prefix did not capture its scalar terminal identity.");
        afterSpecial.Invoke(null, new object?[] { specialState, evaluateMethod, true });
        var specialToken = new RuntimeOrderBindingToken(
            73,
            RuntimeOrderKind.Special,
            specialOrder.Pointer,
            specialController.Pointer,
            specialLifecycle);
        AssertEqual(
            true,
            RuntimeOrderTerminalReceiptStore.TryFind(specialToken, out var evaluatedReceipt),
            "The successful special evaluation did not publish an exact terminal receipt.");
        AssertEqual(RuntimeOrderTerminalDisposition.Evaluated, evaluatedReceipt.Disposition,
            "A successful special evaluation was recorded as generic removal.");
        AssertEqual(RuntimeOrderTerminalReceiptSource.EvaluateOrder, evaluatedReceipt.Source,
            "The special evaluation receipt lost its exact native Hook source.");
        AssertEqual(
            false,
            RuntimeOrderTerminalReceiptStore.TryFind(
                specialToken with { ControllerPointer = (nint)((long)specialController.Pointer + 1) },
                out _),
            "A terminal receipt matched the wrong controller pointer.");

        var manualEvaluateMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
            "EvaulateManualOrder",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The EvaulateManualOrder fixture was not found.");
        var manualOrder = new SpecialOrder(18, -1, "") { IsFullfilled = true };
        var manualController = new SpecialOrderController("梦幻", "无酒精")
        {
            CurrentOrder = manualOrder,
        };
        addSpecial.Invoke(null, new object?[] { manualController, manualOrder, true });
        var manualLifecycle = RequireActiveLifecycle(
            73,
            RuntimeOrderKind.Special,
            manualOrder.Pointer,
            manualController.Pointer,
            "The manual evaluation fixture has no active order lifecycle.");
        var manualArguments = new object?[] { manualController, manualEvaluateMethod, null };
        beforeSpecial.Invoke(null, manualArguments);
        var manualState = RequireTerminalState(
            manualArguments[2],
            "The manual evaluation prefix did not capture its exact terminal identity.");
        var manualToken = new RuntimeOrderBindingToken(
            73,
            RuntimeOrderKind.Special,
            manualOrder.Pointer,
            manualController.Pointer,
            manualLifecycle);
        afterSpecial.Invoke(null, new object?[] { manualState, manualEvaluateMethod, false });
        AssertEqual(
            false,
            RuntimeOrderTerminalReceiptStore.TryFind(manualToken, out _),
            "A skipped manual evaluation published a terminal receipt.");
        afterSpecial.Invoke(null, new object?[] { manualState, manualEvaluateMethod, true });
        AssertEqual(
            true,
            RuntimeOrderTerminalReceiptStore.TryFind(manualToken, out var manualReceipt),
            "The successful manual evaluation did not publish an exact terminal receipt.");
        AssertEqual(RuntimeOrderTerminalReceiptSource.EvaulateManualOrder, manualReceipt.Source,
            "The manual evaluation receipt lost its exact native Hook source.");

        var removeMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
            "RemoveFromOrder",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The RemoveFromOrder fixture was not found.");
        var removeOrder = new SpecialOrder(19, -1, "");
        var removeController = new SpecialOrderController("甜", "无酒精")
        {
            CurrentOrder = removeOrder,
        };
        addSpecial.Invoke(null, new object?[] { removeController, removeOrder, true });
        var removeLifecycle = RequireActiveLifecycle(
            73,
            RuntimeOrderKind.Special,
            removeOrder.Pointer,
            removeController.Pointer,
            "The RemoveFromOrder fixture has no active order lifecycle.");
        var beforeRemove = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "CaptureOrderBeforeRemoval");
        var afterRemove = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnOrderRemovalSucceeded");
        var removeArguments = new object?[] { removeOrder, null };
        beforeRemove.Invoke(null, removeArguments);
        var removeState = RequireTerminalState(
            removeArguments[1],
            "The RemoveFromOrder prefix did not resolve one unique exact capture.");
        var removeToken = new RuntimeOrderBindingToken(
            73,
            RuntimeOrderKind.Special,
            removeOrder.Pointer,
            removeController.Pointer,
            removeLifecycle);
        afterRemove.Invoke(null, new object?[] { removeState, false });
        AssertEqual(1, specialOrders.Count,
            "A skipped native RemoveFromOrder retired the special capture.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(removeToken),
            "A skipped native RemoveFromOrder retired the special active lifecycle.");
        AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(removeToken, out _),
            "A skipped native RemoveFromOrder published a terminal receipt.");

        afterRemove.Invoke(null, new object?[] { removeState, true });
        AssertEqual(
            true,
            RuntimeOrderTerminalReceiptStore.TryFind(removeToken, out var removeReceipt),
            "The successful RemoveFromOrder did not publish an exact terminal receipt.");
        AssertEqual(RuntimeOrderTerminalReceiptSource.RemoveFromOrder, removeReceipt.Source,
            "The removal receipt lost its exact native Hook source.");
        AssertEqual(RuntimeOrderTerminalDisposition.Removed, removeReceipt.Disposition,
            "RemoveFromOrder was misreported as successful evaluation.");

        var addNormal = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "OnControllerOrderAdded");
        var normalRemoveOrder = new NormalOrder();
        var normalRemoveController = new NightScene.GuestManagementUtility.GuestGroupController
        {
            CurrentOrder = normalRemoveOrder,
        };
        addNormal.Invoke(null, new object?[] { normalRemoveController, normalRemoveOrder, true });
        var normalRemoveLifecycle = RequireActiveLifecycle(
            73,
            RuntimeOrderKind.Normal,
            normalRemoveOrder.Pointer,
            normalRemoveController.Pointer,
            "The normal RemoveFromOrder fixture has no active order lifecycle.");
        var beforeNormalRemove = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "CaptureOrderBeforeRemoval");
        var afterNormalRemove = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "OnOrderRemovalSucceeded");
        var normalRemoveArguments = new object?[] { normalRemoveOrder, null };
        beforeNormalRemove.Invoke(null, normalRemoveArguments);
        var normalRemoveState = RequireTerminalState(
            normalRemoveArguments[1],
            "The normal RemoveFromOrder prefix did not resolve one unique exact capture.");
        var normalRemoveToken = new RuntimeOrderBindingToken(
            73,
            RuntimeOrderKind.Normal,
            normalRemoveOrder.Pointer,
            normalRemoveController.Pointer,
            normalRemoveLifecycle);
        afterNormalRemove.Invoke(null, new object?[] { normalRemoveState, false });
        AssertEqual(1, normalOrders.Count,
            "A skipped native RemoveFromOrder retired the normal capture.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(normalRemoveToken),
            "A skipped native RemoveFromOrder retired the normal active lifecycle.");
        AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(normalRemoveToken, out _),
            "A skipped native normal RemoveFromOrder published a terminal receipt.");
        afterNormalRemove.Invoke(null, new object?[] { normalRemoveState, true });
        AssertEqual(0, normalOrders.Count,
            "A successful native RemoveFromOrder did not retire the normal capture.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.TryFind(normalRemoveToken, out var normalRemoveReceipt),
            "A successful native normal RemoveFromOrder did not publish a terminal receipt.");
        AssertEqual(RuntimeOrderTerminalReceiptSource.RemoveFromOrder, normalRemoveReceipt.Source,
            "The normal removal receipt lost its exact native Hook source.");

        var cleanMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
            "CleanOrderInfo",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The CleanOrderInfo fixture was not found.");
        var normalOrder = new NormalOrder();
        var normalController = new NightScene.GuestManagementUtility.GuestGroupController
        {
            CurrentOrder = normalOrder,
        };
        var beforeNormal = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "CaptureControllerOrderBeforeCleanup");
        var afterNormal = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "OnControllerOrderCleanupSucceeded");
        addNormal.Invoke(null, new object?[] { normalController, normalOrder, true });
        var normalLifecycle = RequireActiveLifecycle(
            73,
            RuntimeOrderKind.Normal,
            normalOrder.Pointer,
            normalController.Pointer,
            "The normal cleanup fixture has no active order lifecycle.");
        var normalArguments = new object?[] { normalController, cleanMethod, null };
        beforeNormal.Invoke(null, normalArguments);
        var normalState = RequireTerminalState(
            normalArguments[2],
            "The normal cleanup prefix did not capture its scalar terminal identity.");
        afterNormal.Invoke(null, new object?[] { normalState, cleanMethod, true });
        var normalToken = new RuntimeOrderBindingToken(
            73,
            RuntimeOrderKind.Normal,
            normalOrder.Pointer,
            normalController.Pointer,
            normalLifecycle);
        AssertEqual(
            true,
            RuntimeOrderTerminalReceiptStore.TryFind(normalToken, out var removedReceipt),
            "The successful normal cleanup did not publish an exact terminal receipt.");
        AssertEqual(RuntimeOrderTerminalDisposition.Removed, removedReceipt.Disposition,
            "A normal cleanup was misreported as successful evaluation.");
        AssertEqual(RuntimeOrderTerminalReceiptSource.CleanOrderInfo, removedReceipt.Source,
            "The normal cleanup receipt lost its exact native Hook source.");

        var closingOrder = new SpecialOrder(20, -1, "") { IsFullfilled = true };
        var closingController = new SpecialOrderController("甜", "无酒精")
        {
            CurrentOrder = closingOrder,
        };
        addSpecial.Invoke(null, new object?[] { closingController, closingOrder, true });
        var closingLifecycle = RequireActiveLifecycle(
            73,
            RuntimeOrderKind.Special,
            closingOrder.Pointer,
            closingController.Pointer,
            "The Closing-spanning evaluation fixture has no active order lifecycle.");
        var closingArguments = new object?[] { closingController, evaluateMethod, null };
        beforeSpecial.Invoke(null, closingArguments);
        var closingState = RequireTerminalState(
            closingArguments[2],
            "The evaluation prefix did not latch state before the Closing transition.");
        RuntimeNightBusinessLifecycle.IsActive = false;
        afterSpecial.Invoke(null, new object?[] { closingState, evaluateMethod, true });
        var closingToken = new RuntimeOrderBindingToken(
            73,
            RuntimeOrderKind.Special,
            closingOrder.Pointer,
            closingController.Pointer,
            closingLifecycle);
        AssertEqual(
            true,
            RuntimeOrderTerminalReceiptStore.TryFind(closingToken, out var closingReceipt),
            "A successful native evaluation lost its prefix-latched receipt when Closing began inside the call.");
        AssertEqual(RuntimeOrderTerminalDisposition.Evaluated, closingReceipt.Disposition,
            "The Closing-spanning evaluation receipt lost its exact success disposition.");
        RuntimeNightBusinessLifecycle.IsActive = true;

        var specialSource = File.ReadAllText(FindRepositoryFile(
            "mods", "bepinex", "src", "Save", "SpecialOrderRuntimeCapture.cs"));
        var normalSource = File.ReadAllText(FindRepositoryFile(
            "mods", "bepinex", "src", "Save", "NormalOrderRuntimeCapture.cs"));
        foreach (var source in new[] { specialSource, normalSource })
        {
            AssertContains(
                source,
                "RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycleByOrder(",
                "RemoveFromOrder no longer requires a unique exact active lifecycle for its order pointer.");
            AssertEqual(false, source.Contains("RuntimeKey ==", StringComparison.Ordinal),
                "Terminal receipt publication restored a weak RuntimeKey fallback.");
        }
        var receiptStoreSource = File.ReadAllText(FindRepositoryFile(
            "mods", "bepinex", "src", "Save", "RuntimeOrderTerminalReceiptStore.cs"));
        AssertContains(
            receiptStoreSource,
            "if (found)",
            "The scalar RemoveFromOrder lifecycle lookup no longer rejects ambiguous controller matches.");
    }
    finally
    {
        RuntimeOrderTerminalReceiptStore.Clear();
        specialOrders.Clear();
        normalOrders.Clear();
        RuntimeNightBusinessLifecycle.IsActive = wasActive;
        RuntimeNightBusinessLifecycle.Generation = previousGeneration;
    }
}

static void VerifyCapturedOrderLifecycleAbaKeepsTheNewBinding()
{
    var wasActive = RuntimeNightBusinessLifecycle.IsActive;
    var previousGeneration = RuntimeNightBusinessLifecycle.Generation;
    var specialOrders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var normalOrders = GetCaptureOrders(typeof(NormalOrderRuntimeCapture));
    RuntimeOrderTerminalReceiptStore.Clear();
    specialOrders.Clear();
    normalOrders.Clear();
    try
    {
        RuntimeNightBusinessLifecycle.IsActive = true;
        RuntimeNightBusinessLifecycle.Generation = 79;
        var evaluateMethod = typeof(NightScene.GuestManagementUtility.GuestsManager).GetMethod(
            "EvaluateOrder",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("The EvaluateOrder fixture was not found.");

        var specialOrder = new SpecialOrder(17, -1, "") { IsFullfilled = true };
        var specialController = new SpecialOrderController("甜", "无酒精") { CurrentOrder = specialOrder };
        var addSpecial = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnControllerOrderAdded");
        var beforeSpecial = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "CaptureControllerOrderBeforeCompletion");
        var afterSpecial = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnOrderCompletionSucceeded");
        addSpecial.Invoke(null, new object?[] { specialController, specialOrder, true });
        var specialFirstLifecycle = RequireActiveLifecycle(
            79,
            RuntimeOrderKind.Special,
            specialOrder.Pointer,
            specialController.Pointer,
            "The first special lifecycle was not active.");
        var specialPrefixArguments = new object?[] { specialController, evaluateMethod, null };
        beforeSpecial.Invoke(null, specialPrefixArguments);
        var specialOldState = RequireTerminalState(
            specialPrefixArguments[2],
            "The old special evaluation prefix did not latch its lifecycle.");

        addSpecial.Invoke(null, new object?[] { specialController, specialOrder, true });
        var specialSecondLifecycle = RequireActiveLifecycle(
            79,
            RuntimeOrderKind.Special,
            specialOrder.Pointer,
            specialController.Pointer,
            "The replacement special lifecycle was not active.");
        AssertEqual(true, specialSecondLifecycle > specialFirstLifecycle,
            "Reusing the exact special tuple did not advance its lifecycle.");
        var specialFirstToken = new RuntimeOrderBindingToken(
            79,
            RuntimeOrderKind.Special,
            specialOrder.Pointer,
            specialController.Pointer,
            specialFirstLifecycle);
        var specialSecondToken = specialFirstToken with { LifecycleSequence = specialSecondLifecycle };

        afterSpecial.Invoke(null, new object?[] { specialOldState, evaluateMethod, true });
        AssertEqual(1, specialOrders.Count,
            "An old special postfix removed the replacement lifecycle capture.");
        var retainedSpecial = specialOrders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The retained special ABA capture had an unexpected type.");
        AssertEqual(specialSecondLifecycle, retainedSpecial.OrderLifecycleSequence,
            "An old special postfix replaced the new capture lifecycle.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(specialSecondToken),
            "An old special postfix retired the replacement active lifecycle.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.TryFind(specialFirstToken, out _),
            "The old special postfix did not publish its own lifecycle receipt.");
        AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(specialSecondToken, out _),
            "The old special postfix published a receipt for the replacement lifecycle.");

        var normalOrder = new NormalOrder { IsFullfilled = true };
        var normalController = new NightScene.GuestManagementUtility.GuestGroupController
        {
            CurrentOrder = normalOrder,
        };
        var addNormal = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "OnControllerOrderAdded");
        var beforeNormal = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "CaptureControllerOrderBeforeCompletion");
        var afterNormal = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "OnControllerOrderCompletionSucceeded");
        addNormal.Invoke(null, new object?[] { normalController, normalOrder, true });
        var normalFirstLifecycle = RequireActiveLifecycle(
            79,
            RuntimeOrderKind.Normal,
            normalOrder.Pointer,
            normalController.Pointer,
            "The first normal lifecycle was not active.");
        var normalPrefixArguments = new object?[] { normalController, evaluateMethod, null };
        beforeNormal.Invoke(null, normalPrefixArguments);
        var normalOldState = RequireTerminalState(
            normalPrefixArguments[2],
            "The old normal evaluation prefix did not latch its lifecycle.");

        addNormal.Invoke(null, new object?[] { normalController, normalOrder, true });
        var normalSecondLifecycle = RequireActiveLifecycle(
            79,
            RuntimeOrderKind.Normal,
            normalOrder.Pointer,
            normalController.Pointer,
            "The replacement normal lifecycle was not active.");
        AssertEqual(true, normalSecondLifecycle > normalFirstLifecycle,
            "Reusing the exact normal tuple did not advance its lifecycle.");
        var normalFirstToken = new RuntimeOrderBindingToken(
            79,
            RuntimeOrderKind.Normal,
            normalOrder.Pointer,
            normalController.Pointer,
            normalFirstLifecycle);
        var normalSecondToken = normalFirstToken with { LifecycleSequence = normalSecondLifecycle };

        afterNormal.Invoke(null, new object?[] { normalOldState, evaluateMethod, true });
        AssertEqual(1, normalOrders.Count,
            "An old normal postfix removed the replacement lifecycle capture.");
        var retainedNormal = normalOrders[0] as CapturedRuntimeNormalOrder
            ?? throw new InvalidOperationException("The retained normal ABA capture had an unexpected type.");
        AssertEqual(normalSecondLifecycle, retainedNormal.OrderLifecycleSequence,
            "An old normal postfix replaced the new capture lifecycle.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(normalSecondToken),
            "An old normal postfix retired the replacement active lifecycle.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.TryFind(normalFirstToken, out _),
            "The old normal postfix did not publish its own lifecycle receipt.");
        AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(normalSecondToken, out _),
            "The old normal postfix published a receipt for the replacement lifecycle.");
    }
    finally
    {
        RuntimeOrderTerminalReceiptStore.Clear();
        specialOrders.Clear();
        normalOrders.Clear();
        RuntimeNightBusinessLifecycle.IsActive = wasActive;
        RuntimeNightBusinessLifecycle.Generation = previousGeneration;
    }
}

static void VerifyBoundStatusObserversDoNotAdvanceLifecycle()
{
    var wasActive = RuntimeNightBusinessLifecycle.IsActive;
    var previousGeneration = RuntimeNightBusinessLifecycle.Generation;
    var orders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    RuntimeOrderTerminalReceiptStore.Clear();
    orders.Clear();
    try
    {
        RuntimeNightBusinessLifecycle.IsActive = true;
        RuntimeNightBusinessLifecycle.Generation = 83;
        var order = new SpecialOrder(17, -1, "") { IsFullfilled = true };
        var controller = new SpecialOrderController("甜", "无酒精") { CurrentOrder = order };
        var add = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnControllerOrderAdded");
        var status = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnOrderStatusUpdated");
        var system = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnOrderSystemChanged");
        add.Invoke(null, new object?[] { controller, order, true });
        var lifecycle = RequireActiveLifecycle(
            83,
            RuntimeOrderKind.Special,
            order.Pointer,
            controller.Pointer,
            "The observer fixture has no active lifecycle.");
        var token = new RuntimeOrderBindingToken(
            83,
            RuntimeOrderKind.Special,
            order.Pointer,
            controller.Pointer,
            lifecycle);

        status.Invoke(null, new object?[] { order, "FoodDelivered" });
        system.Invoke(null, new object?[] { new object(), "BeverageDelivered", order });

        AssertEqual(1, orders.Count, "Bound status observers duplicated or removed the current capture.");
        var retained = orders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The observer-retained capture had an unexpected type.");
        AssertEqual(lifecycle, retained.OrderLifecycleSequence,
            "A status observer advanced the captured order lifecycle.");
        AssertEqual(true, RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(token),
            "A status observer replaced or retired the active lifecycle.");
        AssertEqual(
            lifecycle,
            RequireActiveLifecycle(
                83,
                RuntimeOrderKind.Special,
                order.Pointer,
                controller.Pointer,
                "A status observer detached the active lifecycle."),
            "A status observer advanced the store lifecycle sequence.");

        var specialSource = File.ReadAllText(FindRepositoryFile(
            "mods", "bepinex", "src", "Save", "SpecialOrderRuntimeCapture.cs"));
        var normalSource = File.ReadAllText(FindRepositoryFile(
            "mods", "bepinex", "src", "Save", "NormalOrderRuntimeCapture.cs"));
        AssertLifecycleStartIsIsolated(
            specialSource,
            "private static long BeginOrderLifecycle(",
            "private static CapturedRuntimeSpecialOrder? AttachActiveOrderLifecycle(",
            "special");
        AssertLifecycleStartIsIsolated(
            normalSource,
            "private static long BeginOrderLifecycle(",
            "private static TerminalOrderCaptureState? CaptureControllerTerminalState(",
            "normal");

        var observerStart = specialSource.IndexOf(
            "private static CapturedRuntimeSpecialOrder? AttachActiveOrderLifecycle(",
            StringComparison.Ordinal);
        var observerEnd = observerStart < 0
            ? -1
            : specialSource.IndexOf(
                "private static bool TryReadOrderLifecycleIdentity(",
                observerStart,
                StringComparison.Ordinal);
        AssertEqual(true, observerStart >= 0 && observerEnd > observerStart,
            "The special observer lifecycle source block was not found.");
        var observerSource = specialSource[observerStart..observerEnd];
        AssertContains(observerSource, "TryCaptureActiveLifecycle",
            "The bound special observer no longer attaches the active lifecycle.");
        AssertEqual(false, observerSource.Contains("BeginLifecycle", StringComparison.Ordinal),
            "The bound special observer starts a new lifecycle.");
        AssertEqual(false, observerSource.Contains("BeginOrderLifecycle", StringComparison.Ordinal),
            "The bound special observer calls the canonical lifecycle-start helper.");
    }
    finally
    {
        RuntimeOrderTerminalReceiptStore.Clear();
        orders.Clear();
        RuntimeNightBusinessLifecycle.IsActive = wasActive;
        RuntimeNightBusinessLifecycle.Generation = previousGeneration;
    }
}

static void VerifyRawTagDriftQuarantinesTheLifecycle()
{
    var wasActive = RuntimeNightBusinessLifecycle.IsActive;
    var previousGeneration = RuntimeNightBusinessLifecycle.Generation;
    var orders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    RuntimeOrderTerminalReceiptStore.Clear();
    orders.Clear();
    try
    {
        RuntimeNightBusinessLifecycle.IsActive = true;
        RuntimeNightBusinessLifecycle.Generation = 87;
        var order = new SpecialOrder(17, -1, "");
        var controller = new SpecialOrderController("甜", "无酒精") { CurrentOrder = order };
        var add = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnControllerOrderAdded");
        var status = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnOrderStatusUpdated");
        var system = GetCaptureMutation(typeof(SpecialOrderRuntimeCapture), "OnOrderSystemChanged");

        foreach (var (foodTagId, beverageTagId, fulfilled, context, useSystemObserver, label) in new[]
                 {
                     (18, -1, false, "FoodDelivered", false, "non-fulfilled food"),
                     (17, 9, true, "OrderChanged", true, "non-delivery beverage"),
                 })
        {
            order.RequestFoodTag = 17;
            order.RequestBeverageTag = -1;
            order.IsFullfilled = false;
            add.Invoke(null, new object?[] { controller, order, true });
            AssertEqual(1, orders.Count, $"The {label}-drift fixture did not create its initial capture.");
            var captured = orders[0] as CapturedRuntimeSpecialOrder
                ?? throw new InvalidOperationException($"The {label}-drift capture had an unexpected type.");
            var token = new RuntimeOrderBindingToken(
                87,
                RuntimeOrderKind.Special,
                order.Pointer,
                controller.Pointer,
                captured.OrderLifecycleSequence);

            order.RequestFoodTag = foodTagId;
            order.RequestBeverageTag = beverageTagId;
            order.IsFullfilled = fulfilled;
            if (useSystemObserver)
            {
                system.Invoke(null, new object?[] { new object(), context, order });
            }
            else
            {
                status.Invoke(null, new object?[] { order, context });
            }

            AssertEqual(0, orders.Count,
                $"A same-lifecycle {label} Tag drift remained available to provider/trace projection.");
            AssertEqual(0, SpecialOrderRuntimeCapture.Snapshot(TimeSpan.FromMinutes(1)).Count,
                $"A same-lifecycle {label} Tag drift remained in the authoritative projection snapshot.");
            AssertEqual(false, RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(token),
                $"A same-lifecycle {label} Tag drift remained active for named live-controller automation.");
            AssertEqual(false, RuntimeOrderTerminalReceiptStore.TryFind(token, out _),
                $"A same-lifecycle {label} Tag drift was fabricated as a terminal order fact.");
        }

        order.RequestFoodTag = 17;
        order.RequestBeverageTag = -1;
        order.IsFullfilled = false;
        add.Invoke(null, new object?[] { controller, order, true });
        AssertEqual(1, orders.Count,
            "A new successful native binding did not recover after the corrupt lifecycle was quarantined.");
        var recovered = orders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The recovered raw-identity capture had an unexpected type.");
        AssertEqual(17, recovered.FoodTagId,
            "The recovered lifecycle inherited the quarantined food Tag identity.");
        AssertEqual(-1, recovered.BeverageTagId,
            "The recovered lifecycle inherited the quarantined beverage Tag identity.");
        AssertEqual(
            true,
            SpecialOrderRuntimeCapture.RecentParseFailuresSnapshot(TimeSpan.FromMinutes(1), 16)
                .Any(message => message.Contains(
                    "raw RequestFoodTag/RequestBeverageTag identity changed within one order lifecycle",
                    StringComparison.Ordinal)),
            "Raw Tag drift did not leave a bounded fail-closed diagnostic.");

        var source = File.ReadAllText(FindRepositoryFile(
            "mods", "bepinex", "src", "Save", "SpecialOrderRuntimeCapture.cs"));
        var observerStart = source.IndexOf(
            "private static void UpdateOrderStatus(",
            StringComparison.Ordinal);
        var observerEnd = observerStart < 0
            ? -1
            : source.IndexOf(
                "private static void UpdateExistingOrder(",
                observerStart,
                StringComparison.Ordinal);
        AssertEqual(true, observerStart >= 0 && observerEnd > observerStart,
            "The production special-order status observer block was not found.");
        var observerSource = source[observerStart..observerEnd];
        var quarantineIndex = observerSource.IndexOf(
            "TryQuarantineRawTagIdentityConflict(",
            StringComparison.Ordinal);
        var contextGateIndex = observerSource.IndexOf(
            "IsOrderDeliveryContext(",
            StringComparison.Ordinal);
        var fulfilledGateIndex = observerSource.IndexOf(
            "!order.IsFulfilled",
            StringComparison.Ordinal);
        AssertEqual(
            true,
            quarantineIndex >= 0
            && contextGateIndex > quarantineIndex
            && fulfilledGateIndex > quarantineIndex,
            "Raw Tag quarantine no longer runs before every production status-observer gate.");
    }
    finally
    {
        RuntimeOrderTerminalReceiptStore.Clear();
        orders.Clear();
        RuntimeNightBusinessLifecycle.IsActive = wasActive;
        RuntimeNightBusinessLifecycle.Generation = previousGeneration;
    }
}

static void AssertLifecycleStartIsIsolated(
    string source,
    string beginMarker,
    string endMarker,
    string label)
{
    AssertEqual(
        1,
        System.Text.RegularExpressions.Regex.Matches(
            source,
            @"RuntimeOrderTerminalReceiptStore\.BeginLifecycle\(").Count,
        $"The {label} capture has a lifecycle start outside its one canonical helper.");
    AssertEqual(
        3,
        System.Text.RegularExpressions.Regex.Matches(source, @"\bBeginOrderLifecycle\(").Count,
        $"The {label} lifecycle-start helper is no longer limited to its definition and two binding callbacks.");
    var beginStart = source.IndexOf(beginMarker, StringComparison.Ordinal);
    var beginEnd = beginStart < 0
        ? -1
        : source.IndexOf(endMarker, beginStart, StringComparison.Ordinal);
    var lifecycleStart = source.IndexOf(
        "RuntimeOrderTerminalReceiptStore.BeginLifecycle(",
        StringComparison.Ordinal);
    AssertEqual(
        true,
        beginStart >= 0 && beginEnd > beginStart && lifecycleStart > beginStart && lifecycleStart < beginEnd,
        $"The {label} lifecycle start is not isolated inside BeginOrderLifecycle.");
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
    AssertContains(normalSnapshot, "capturedNativeKeys",
        "Authoritative normal captures no longer exclude unbound HUD rows with the same native key.");
    AssertContains(normalSnapshot, "|lifecycle:{order.OrderLifecycleSequence}",
        "Normal-order projection groups no longer isolate reused native pointers by lifecycle.");
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
        var specialManualOrder = new SpecialOrder(17, -1, "", manualOrder: true);
        var specialManualController = new SpecialOrderController("甜", "无酒精")
        {
            CurrentOrder = specialManualOrder,
        };
        specialSetter.Invoke(
            null,
            new object?[]
            {
                specialManualController,
                specialCallback,
                specialManualOrder,
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

        var normalManualOrder = new NormalOrder(manualOrder: true);
        var normalManualController = new SpecialOrderController("", "")
        {
            CurrentOrder = normalManualOrder,
        };
        normalSetter.Invoke(
            null,
            new object?[]
            {
                normalManualController,
                normalCallback,
                normalManualOrder,
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
        var specialMissingCallbackOrder = new SpecialOrder(17, -1, "", manualOrder: true);
        var specialMissingCallbackController = new SpecialOrderController("甜", "无酒精")
        {
            CurrentOrder = specialMissingCallbackOrder,
        };
        specialSetter.Invoke(
            null,
            new object?[]
            {
                specialMissingCallbackController,
                null,
                specialMissingCallbackOrder,
                true,
            });
        var missingCallback = specialOrders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The incomplete special manual capture had an unexpected type.");
        AssertEqual(true, missingCallback.ManualOrder, "A missing callback erased the manual-order identity.");
        AssertEqual<object?>(null, missingCallback.ManualEvaluationCallback, "A missing callback was replaced by a fallback object.");
        AssertEqual(true, missingCallback.ManualEvaluationBindingObserved, "A special setter with a missing callback was hidden.");
        AssertEqual<object?>(null, missingCallback.ManualEvaluationBindingCallback, "A missing special binding callback was replaced.");

        normalOrders.Clear();
        var normalMissingCallbackOrder = new NormalOrder(manualOrder: true);
        var normalMissingCallbackController = new SpecialOrderController("", "")
        {
            CurrentOrder = normalMissingCallbackOrder,
        };
        normalSetter.Invoke(
            null,
            new object?[]
            {
                normalMissingCallbackController,
                null,
                normalMissingCallbackOrder,
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
        2, 1003, "Yuuma", 30, -1,
        false, false, capturedAt, capturedAt, "ptr:reused", "ManualOrderSet")
    {
        OrderObject = new object(),
        ControllerObject = new object(),
        ManualOrder = true,
        ManualEvaluationCallback = callback,
        ManualEvaluationBindingObserved = true,
        ManualEvaluationBindingCallback = callback,
        OrderLifecycleSequence = 1,
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
        OrderLifecycleSequence = 1,
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
                OrderLifecycleSequence = 2,
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
                OrderLifecycleSequence = 2,
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
    var specialRemove = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "RemoveOrder",
        BindingFlags.NonPublic | BindingFlags.Static,
        binder: null,
        types: new[] { typeof(RuntimeOrderBindingToken), typeof(string) },
        modifiers: null)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.RemoveOrder(binding, source) was not found.");
    var normalAdd = GetCaptureMutation(typeof(NormalOrderRuntimeCapture), "AddOrder");
    var normalRemove = typeof(NormalOrderRuntimeCapture).GetMethod(
        "RemoveOrder",
        BindingFlags.NonPublic | BindingFlags.Static,
        binder: null,
        types: new[] { typeof(RuntimeOrderBindingToken) },
        modifiers: null)
        ?? throw new InvalidOperationException("NormalOrderRuntimeCapture.RemoveOrder(binding) was not found.");
    var orderObject = new object();
    var controllerObject = new object();

    var specialManual = new CapturedRuntimeSpecialOrder(
        2, 1003, "Yuuma", 30, -1,
        false, false, capturedAt, capturedAt, "ptr:abc", "ManualOrderSet")
    {
        OrderObject = orderObject,
        ControllerObject = controllerObject,
        ManualOrder = true,
        ManualEvaluationCallback = callback,
        ManualEvaluationBindingObserved = true,
        ManualEvaluationBindingCallback = callback,
        OrderLifecycleSequence = 1,
    };
    var normalManual = new CapturedRuntimeNormalOrder(
        "ptr:def", 2, "Yuuma", 17, 3, capturedAt, capturedAt, "ManualOrderSet")
    {
        OrderObject = orderObject,
        ControllerObject = controllerObject,
        ManualOrder = true,
        ManualEvaluationCallback = callback,
        ManualEvaluationBindingObserved = true,
        ManualEvaluationBindingCallback = callback,
        OrderLifecycleSequence = 1,
    };

    specialOrders.Clear();
    normalOrders.Clear();
    try
    {
        specialAdd.Invoke(null, new object?[] { specialManual });
        specialRemove.Invoke(null, new object?[]
        {
            new RuntimeOrderBindingToken(1, RuntimeOrderKind.Special, (nint)0xabc, (nint)0x111, 1),
            "Test",
        });
        specialAdd.Invoke(null, new object?[]
        {
            specialManual with
            {
                CaptureSource = "OrderAdd",
                ManualOrder = false,
                ManualEvaluationCallback = null,
                ManualEvaluationBindingObserved = false,
                ManualEvaluationBindingCallback = null,
                OrderLifecycleSequence = 2,
            },
        });
        var specialReused = specialOrders[0] as CapturedRuntimeSpecialOrder
            ?? throw new InvalidOperationException("The reused special capture had an unexpected type.");
        AssertEqual(false, specialReused.ManualEvaluationBindingObserved, "A retired special binding survived exact pointer and identity reuse.");
        AssertEqual<object?>(null, specialReused.ManualEvaluationBindingCallback, "A retired special callback survived exact pointer and identity reuse.");

        normalAdd.Invoke(null, new object?[] { normalManual });
        normalRemove.Invoke(null, new object?[]
        {
            new RuntimeOrderBindingToken(1, RuntimeOrderKind.Normal, (nint)0xdef, (nint)0x222, 1),
        });
        normalAdd.Invoke(null, new object?[]
        {
            normalManual with
            {
                CaptureSource = "OrderAdd",
                ManualOrder = false,
                ManualEvaluationCallback = null,
                ManualEvaluationBindingObserved = false,
                ManualEvaluationBindingCallback = null,
                OrderLifecycleSequence = 2,
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

static object RequireTerminalState(object? state, string message)
{
    if (state == null)
    {
        throw new InvalidOperationException(message);
    }

    return state;
}

static long RequireActiveLifecycle(
    long businessGeneration,
    RuntimeOrderKind orderKind,
    nint orderPointer,
    nint controllerPointer,
    string message)
{
    if (!RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycle(
            businessGeneration,
            orderKind,
            orderPointer,
            controllerPointer,
            out var lifecycleSequence))
    {
        throw new InvalidOperationException(message);
    }

    return lifecycleSequence;
}

static void AssertNoActiveLifecycle(
    long businessGeneration,
    RuntimeOrderKind orderKind,
    nint orderPointer,
    nint controllerPointer,
    string message)
{
    AssertEqual(
        false,
        RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycle(
            businessGeneration,
            orderKind,
            orderPointer,
            controllerPointer,
            out _),
        message);
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
    RuntimeOrderTerminalReceiptStore.Clear();
    var specialOrders = GetCaptureOrders(typeof(SpecialOrderRuntimeCapture));
    var normalOrders = GetCaptureOrders(typeof(NormalOrderRuntimeCapture));
    var specialSetter = GetManualSetter(typeof(SpecialOrderRuntimeCapture));
    var normalSetter = GetManualSetter(typeof(NormalOrderRuntimeCapture));
    var specialOrder = new SpecialOrder(17, -1, "", manualOrder: true);
    var specialController = new SpecialOrderController("甜", "无酒精") { CurrentOrder = specialOrder };
    var normalOrder = new NormalOrder(manualOrder: true);
    var normalController = new NightScene.GuestManagementUtility.GuestGroupController
    {
        CurrentOrder = normalOrder,
    };

    specialOrders.Clear();
    normalOrders.Clear();
    try
    {
        specialSetter.Invoke(
            null,
            new object?[] { specialController, new object(), specialOrder, false });
        AssertEqual(0, specialOrders.Count,
            "A skipped native special manual setter produced a captured order.");
        AssertNoActiveLifecycle(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Special,
            specialOrder.Pointer,
            specialController.Pointer,
            "A skipped native special manual setter started an active lifecycle.");

        normalSetter.Invoke(
            null,
            new object?[] { normalController, new object(), normalOrder, false });
        AssertEqual(0, normalOrders.Count,
            "A skipped native normal manual setter produced a captured order.");
        AssertNoActiveLifecycle(
            RuntimeNightBusinessLifecycle.Generation,
            RuntimeOrderKind.Normal,
            normalOrder.Pointer,
            normalController.Pointer,
            "A skipped native normal manual setter started an active lifecycle.");
    }
    finally
    {
        specialOrders.Clear();
        normalOrders.Clear();
        RuntimeOrderTerminalReceiptStore.Clear();
    }
}

static CapturedRuntimeSpecialOrder Parse(SpecialOrder order, object controller)
{
    return TryParse(order, controller)
        ?? throw new InvalidOperationException("The special order was not captured.");
}

static CapturedRuntimeSpecialOrder? TryParse(SpecialOrder order, object controller)
{
    var parseOrder = typeof(SpecialOrderRuntimeCapture).GetMethod(
        "ParseOrder",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SpecialOrderRuntimeCapture.ParseOrder was not found.");
    return parseOrder.Invoke(null, new[] { order, "Smoke", controller }) as CapturedRuntimeSpecialOrder;
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
    public bool ThrowOnTextRead { get; init; }

    public string GetOrderFoodText(SpecialOrder order)
    {
        FoodReads++;
        if (ThrowOnTextRead) throw new InvalidOperationException("Food text getter must not run.");
        return _foodTag;
    }

    public string GetOrderBevText(SpecialOrder order)
    {
        BeverageReads++;
        if (ThrowOnTextRead) throw new InvalidOperationException("Beverage text getter must not run.");
        return _beverageTag;
    }
}
