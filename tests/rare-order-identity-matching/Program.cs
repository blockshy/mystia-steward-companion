using MystiaStewardCompanion.Save;

try
{
    VerifyDisplayOverrideDoesNotAffectIdentity();
    VerifyTagConflictIsRejected();
    VerifyNegativeTagIdIsPreserved();
    VerifyMappedGuestUsesRuntimeIdentity();
    VerifyMissingIdentityFailsClosed();
    VerifyConcurrentYuyukoRetakeOrdersRemainIsolated();
    VerifyConcurrentYuumaOrdersRemainIsolated();
    VerifyCapturedControllerOwnershipDoesNotDependOnManagerScan();
    VerifyFulfilledCapturedOrderDependsOnLookupPurpose();
    VerifyCookingTargetIdentity();
    Console.WriteLine("PASS: rare-order matching uses complete runtime identity and captured-order liveness.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyDisplayOverrideDoesNotAffectIdentity()
{
    const string requestedTag = "灼热";
    const string gameDisplayOverride = "料理就和魔法一样，发光发热才叫好！";
    AssertTrue(requestedTag != gameDisplayOverride, "The fixture must use different request and display text.");

    var expected = new RareOrderIdentity(7, 10, 3, 21);
    var captured = new RareOrderIdentity(7, 10, 3, 21);
    AssertMatch(expected, captured, "Display text must not participate in identity matching.");
}

static void VerifyTagConflictIsRejected()
{
    var expected = new RareOrderIdentity(7, 10, 3, 21);
    AssertReject(expected, new RareOrderIdentity(7, 10, 4, 21), "food tag id mismatch");
    AssertReject(expected, new RareOrderIdentity(7, 10, 3, 22), "beverage tag id mismatch");
}

static void VerifyNegativeTagIdIsPreserved()
{
    var expected = new RareOrderIdentity(2, 123, 17, -1);
    var captured = new RareOrderIdentity(2, 123, 17, -1);
    AssertMatch(expected, captured, "A negative runtime Tag ID is a valid identity value.");
}

static void VerifyMappedGuestUsesRuntimeIdentity()
{
    const int normalizedCatalogGuestId = 23;
    const int runtimeVariantGuestId = 1023;
    AssertTrue(normalizedCatalogGuestId != runtimeVariantGuestId, "The fixture must model a mapped runtime guest.");

    var expected = new RareOrderIdentity(4, runtimeVariantGuestId, 30, -1);
    var captured = new RareOrderIdentity(4, runtimeVariantGuestId, 30, -1);
    AssertMatch(expected, captured, "Mapped guests must match by their raw runtime ID, not their normalized catalog ID.");
}

static void VerifyMissingIdentityFailsClosed()
{
    var expected = new RareOrderIdentity(2, 123, 17, -1);
    AssertReject(expected, new RareOrderIdentity(2, 123, null, -1), "candidate food tag id missing");
    AssertReject(new RareOrderIdentity(2, 123, 17, null), expected, "request beverage tag id missing");
}

static void VerifyConcurrentYuyukoRetakeOrdersRemainIsolated()
{
    // Runtime identities observed for R-0082 and R-0083 in one Yuyuko retake phase-three session.
    var r0082 = new RareOrderIdentity(1, 40, 14, 2);
    var r0083 = new RareOrderIdentity(0, 23, 4, 2);

    AssertMatch(r0082, new RareOrderIdentity(1, 40, 14, 2), "R-0082 lost its exact raw identity.");
    AssertMatch(r0083, new RareOrderIdentity(0, 23, 4, 2), "R-0083 lost its exact raw identity.");
    AssertReject(r0082, r0083, "desk mismatch");
    AssertReject(r0083, r0082, "desk mismatch");
    AssertReject(r0082, r0082 with { RuntimeGuestId = 23 }, "runtime guest id mismatch");
    AssertReject(r0083, r0083 with { FoodTagId = 14 }, "food tag id mismatch");
}

static void VerifyConcurrentYuumaOrdersRemainIsolated()
{
    const int yuumaRuntimeId = 1003;
    var firstDesk = new RareOrderIdentity(0, yuumaRuntimeId, 3, 21);
    var secondDesk = new RareOrderIdentity(1, yuumaRuntimeId, 3, 21);
    var changedRequest = new RareOrderIdentity(0, yuumaRuntimeId, 4, 21);

    AssertMatch(
        firstDesk,
        new RareOrderIdentity(0, yuumaRuntimeId, 3, 21),
        "A Yuuma order lost its exact desk/runtime/tag identity.");
    AssertReject(firstDesk, secondDesk, "desk mismatch");
    AssertReject(firstDesk, changedRequest, "food tag id mismatch");
    AssertReject(
        firstDesk,
        firstDesk with { RuntimeGuestId = null },
        "candidate runtime guest id missing");
}

static void VerifyCapturedControllerOwnershipDoesNotDependOnManagerScan()
{
    const int scannedManagerOrders = 0;
    const bool capturedControllerOwnsOrder = true;
    AssertTrue(scannedManagerOrders == 0, "The fixture must model an empty manager scan.");
    AssertExecutable(
        hasOrderObject: true,
        hasControllerObject: true,
        isFulfilled: false,
        isOwnedByController: capturedControllerOwnsOrder,
        "A captured controller-owned order must remain executable when the manager scan is empty.");
}

static void VerifyFulfilledCapturedOrderDependsOnLookupPurpose()
{
    var accepted = RareOrderIdentityMatcher.IsExecutableCapturedOrder(
        hasOrderObject: true,
        hasControllerObject: true,
        isFulfilled: true,
        isOwnedByController: true,
        allowFulfilled: false,
        out var reason);
    AssertTrue(!accepted, "A fulfilled captured order was accepted for delivery.");
    AssertTrue(reason.Contains("fulfilled", StringComparison.Ordinal), $"Unexpected rejection reason: {reason}");

    accepted = RareOrderIdentityMatcher.IsExecutableCapturedOrder(
        hasOrderObject: true,
        hasControllerObject: true,
        isFulfilled: true,
        isOwnedByController: true,
        allowFulfilled: true,
        out reason);
    AssertTrue(accepted, $"A fulfilled captured order was rejected for completion/evaluation. Reason: {reason}");
}

static void VerifyCookingTargetIdentity()
{
    var identity = new RareOrderIdentity(2, 123, 17, -1);
    AssertTrue(
        RareOrderIdentityMatcher.IsSameCookingTarget("R-0145", 88, identity, "R-0145", 88, identity),
        "The same trace and food target were not recognized as one cooking target.");
    AssertTrue(
        !RareOrderIdentityMatcher.IsSameCookingTarget("R-0145", 88, identity, "R-0145", 88, identity with { BeverageTagId = 14 }),
        "The same trace bypassed a conflicting raw order identity.");
    AssertTrue(
        !RareOrderIdentityMatcher.IsSameCookingTarget("R-0145", 88, identity, "R-0146", 88, identity),
        "Different traces were merged despite matching raw fields.");
    AssertTrue(
        !RareOrderIdentityMatcher.IsSameCookingTarget("R-0145", 88, identity, null, 88, identity),
        "A traced target was merged with an untraced target.");
    AssertTrue(
        !RareOrderIdentityMatcher.IsSameCookingTarget("R-0145", 88, identity, "R-0145", 89, identity),
        "The same trace bypassed a conflicting cooked food target.");
    AssertTrue(
        RareOrderIdentityMatcher.IsSameCookingTarget(null, 88, identity, null, 88, identity),
        "Equal complete raw identities did not provide the untraced target fallback.");
    AssertTrue(
        !RareOrderIdentityMatcher.IsSameCookingTarget(null, 88, identity, null, 88, identity with { FoodTagId = null }),
        "An incomplete raw identity was accepted for untraced target matching.");
    AssertTrue(
        !RareOrderIdentityMatcher.IsSameCookingTarget(null, 88, identity, null, 88, identity with { RuntimeGuestId = 999 }),
        "A conflicting runtime guest ID was accepted for untraced target matching.");
}

static void AssertMatch(RareOrderIdentity expected, RareOrderIdentity candidate, string message)
{
    var matched = RareOrderIdentityMatcher.Matches(expected, candidate, out var reason);
    AssertTrue(matched, $"{message} Reason: {reason}");
}

static void AssertReject(RareOrderIdentity expected, RareOrderIdentity candidate, string expectedReason)
{
    var matched = RareOrderIdentityMatcher.Matches(expected, candidate, out var reason);
    AssertTrue(!matched, $"Identity unexpectedly matched: {RareOrderIdentityMatcher.Format(candidate)}");
    AssertTrue(reason.Contains(expectedReason, StringComparison.Ordinal), $"Expected '{expectedReason}', actual '{reason}'.");
}

static void AssertExecutable(
    bool hasOrderObject,
    bool hasControllerObject,
    bool? isFulfilled,
    bool isOwnedByController,
    string message)
{
    var executable = RareOrderIdentityMatcher.IsExecutableCapturedOrder(
        hasOrderObject,
        hasControllerObject,
        isFulfilled,
        isOwnedByController,
        allowFulfilled: false,
        out var reason);
    AssertTrue(executable, $"{message} Reason: {reason}");
}

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
