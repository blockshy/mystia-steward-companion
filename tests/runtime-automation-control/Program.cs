using System.Text.Json;
using MystiaStewardCompanion.Save;

try
{
    VerifyAuthorityAndLeaseGate();
    VerifyCurrentStageConfiguration();
    VerifySpecialOverrideScope();
    VerifyLeaseExpiryAndRevocation();
    VerifyProfileShapeAndRevisionValidation();
    VerifyPermitSerializesAuthorityTransition();
    Console.WriteLine(
        "PASS: runtime automation control requires the exact current authority lease, evaluates "
        + "delivery/completion switches at each future side-effect boundary, preserves the Koishi "
        + "stage-only override, and serializes authority transitions with admitted native effects.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyAuthorityAndLeaseGate()
{
    var now = Utc(12, 0, 0);
    RuntimeAutomationControlState.Reset("test reset");
    AssertDecision(
        RuntimeAutomationControlState.Observe(
            RuntimeAutomationControlTargetKind.Rare,
            RuntimeAutomationControlStage.FoodDelivery,
            forceStageConfiguration: false,
            now),
        allowed: false,
        state: "suspended-authority",
        reasonCode: "automation-profile-unavailable",
        authorityRevision: 0);

    var enabled = Profile();
    RuntimeAutomationControlState.PublishAuthority(
        enabled,
        authorityRevision: 7,
        "automation-primary-device-changing",
        "primary changing");
    AssertDecision(
        RuntimeAutomationControlState.Observe(
            RuntimeAutomationControlTargetKind.Normal,
            RuntimeAutomationControlStage.FoodDelivery,
            forceStageConfiguration: false,
            now),
        allowed: false,
        state: "suspended-authority",
        reasonCode: "automation-primary-device-changing",
        authorityRevision: 7);

    AssertThrows<InvalidOperationException>(
        () => RuntimeAutomationControlState.PublishLease(6, now.AddMinutes(1)),
        "A lease for a stale authority revision was accepted.");
    RuntimeAutomationControlState.PublishLease(7, now.AddMinutes(1));
    AssertAllowed(RuntimeAutomationControlTargetKind.Rare, RuntimeAutomationControlStage.FoodDelivery, now);
    AssertAllowed(RuntimeAutomationControlTargetKind.Normal, RuntimeAutomationControlStage.OrderEvaluation, now);

    RuntimeAutomationControlState.PublishAuthority(
        enabled,
        authorityRevision: 7,
        "registration-poll",
        "registration poll");
    AssertAllowed(RuntimeAutomationControlTargetKind.Normal, RuntimeAutomationControlStage.FoodDelivery, now);

    RuntimeAutomationControlState.PublishAuthority(
        Profile(autoNormalDeliverFood: false),
        authorityRevision: 7,
        "automation-profile-changing",
        "profile changing");
    AssertDecision(
        RuntimeAutomationControlState.Observe(
            RuntimeAutomationControlTargetKind.Normal,
            RuntimeAutomationControlStage.FoodDelivery,
            forceStageConfiguration: false,
            now),
        allowed: false,
        state: "suspended-authority",
        reasonCode: "automation-profile-changing",
        authorityRevision: 7);
}

static void VerifyCurrentStageConfiguration()
{
    var now = Utc(12, 5, 0);
    Publish(Profile(autoPrepCollectCooking: false), 8, now);
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Rare, RuntimeAutomationControlStage.FoodDelivery, now),
        false,
        "suspended-configuration",
        "rare-food-delivery-disabled",
        8);
    AssertAllowed(RuntimeAutomationControlTargetKind.Rare, RuntimeAutomationControlStage.OrderEvaluation, now);
    AssertAllowed(RuntimeAutomationControlTargetKind.Normal, RuntimeAutomationControlStage.FoodDelivery, now);

    Publish(Profile(autoPrepCompleteOrder: false), 9, now);
    AssertAllowed(RuntimeAutomationControlTargetKind.Rare, RuntimeAutomationControlStage.FoodDelivery, now);
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Rare, RuntimeAutomationControlStage.OrderEvaluation, now),
        false,
        "suspended-configuration",
        "rare-order-completion-disabled",
        9);

    Publish(Profile(autoNormalDeliverFood: false), 10, now);
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Normal, RuntimeAutomationControlStage.FoodDelivery, now),
        false,
        "suspended-configuration",
        "normal-food-delivery-disabled",
        10);

    Publish(Profile(autoNormalCompleteOrder: false), 11, now);
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Normal, RuntimeAutomationControlStage.OrderEvaluation, now),
        false,
        "suspended-configuration",
        "normal-order-completion-disabled",
        11);

    Publish(Profile(autoRareOrderEnabled: false), 12, now);
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Rare, RuntimeAutomationControlStage.FoodDelivery, now),
        false,
        "suspended-configuration",
        "rare-automation-disabled",
        12);

    Publish(Profile(autoNormalOrderEnabled: false), 13, now);
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Normal, RuntimeAutomationControlStage.OrderEvaluation, now),
        false,
        "suspended-configuration",
        "normal-automation-disabled",
        13);

    Publish(Profile(automationEnabled: false), 14, now);
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Rare, RuntimeAutomationControlStage.FoodDelivery, now),
        false,
        "suspended-configuration",
        "automation-disabled",
        14);
}

static void VerifySpecialOverrideScope()
{
    var now = Utc(12, 10, 0);
    Publish(
        Profile(autoPrepCollectCooking: false, autoPrepCompleteOrder: false),
        15,
        now);
    AssertTrue(
        RuntimeAutomationControlState.Observe(
            RuntimeAutomationControlTargetKind.Rare,
            RuntimeAutomationControlStage.YuumaSettlement,
            forceStageConfiguration: true,
            now).Allowed,
        "The named Koishi full-feed path no longer bypasses only its stage switches.");

    Publish(
        Profile(automationEnabled: false, autoPrepCollectCooking: false, autoPrepCompleteOrder: false),
        16,
        now);
    AssertDecision(
        RuntimeAutomationControlState.Observe(
            RuntimeAutomationControlTargetKind.Rare,
            RuntimeAutomationControlStage.YuumaSettlement,
            forceStageConfiguration: true,
            now),
        false,
        "suspended-configuration",
        "automation-disabled",
        16);

    Publish(
        Profile(autoRareOrderEnabled: false, autoPrepCollectCooking: false, autoPrepCompleteOrder: false),
        17,
        now);
    AssertDecision(
        RuntimeAutomationControlState.Observe(
            RuntimeAutomationControlTargetKind.Rare,
            RuntimeAutomationControlStage.YuumaSettlement,
            forceStageConfiguration: true,
            now),
        false,
        "suspended-configuration",
        "rare-automation-disabled",
        17);
}

static void VerifyLeaseExpiryAndRevocation()
{
    var now = Utc(12, 15, 0);
    RuntimeAutomationControlState.PublishAuthority(Profile(), 18, "profile-changing", "profile changing");
    RuntimeAutomationControlState.PublishLease(18, now);
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Normal, RuntimeAutomationControlStage.FoodDelivery, now),
        false,
        "suspended-authority",
        "automation-lease-expired",
        18);

    RuntimeAutomationControlState.PublishLease(18, now.AddMinutes(1));
    RuntimeAutomationControlState.RevokeLease("automation-lease-released", "lease released");
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Normal, RuntimeAutomationControlStage.FoodDelivery, now),
        false,
        "suspended-authority",
        "automation-lease-released",
        18);
}

static void VerifyProfileShapeAndRevisionValidation()
{
    AssertThrows<ArgumentOutOfRangeException>(
        () => RuntimeAutomationControlState.PublishAuthority(Profile(), 0, "invalid", "invalid"),
        "A non-positive authority revision was accepted.");
    AssertThrows<InvalidDataException>(
        () => RuntimeAutomationControlState.PublishAuthority(
            JsonSerializer.SerializeToElement(new { automationEnabled = true }),
            19,
            "invalid",
            "invalid"),
        "A profile missing exact automation booleans was accepted.");
    AssertThrows<InvalidDataException>(
        () => RuntimeAutomationControlState.PublishAuthority(
            JsonSerializer.SerializeToElement(new { automationEnabled = "true" }),
            19,
            "invalid",
            "invalid"),
        "A non-boolean automation profile field was accepted.");
}

static void VerifyPermitSerializesAuthorityTransition()
{
    var now = Utc(12, 20, 0);
    Publish(Profile(), 20, now);
    using var permit = RuntimeAutomationControlState.AcquirePermit(
        RuntimeAutomationControlTargetKind.Normal,
        RuntimeAutomationControlStage.FoodDelivery,
        forceStageConfiguration: false,
        now);
    AssertTrue(permit.Allowed, "The exact native side-effect boundary did not acquire its permit.");

    using var transitionStarted = new ManualResetEventSlim();
    var transition = Task.Run(() =>
    {
        transitionStarted.Set();
        RuntimeAutomationControlState.PublishAuthority(
            Profile(),
            21,
            "automation-primary-device-changing",
            "primary changing");
    });
    AssertTrue(transitionStarted.Wait(TimeSpan.FromSeconds(2)), "The authority transition did not start.");
    AssertTrue(
        !transition.Wait(TimeSpan.FromMilliseconds(100)),
        "An authority transition tore an already admitted native side-effect boundary.");
    permit.Dispose();
    AssertTrue(
        transition.Wait(TimeSpan.FromSeconds(2)),
        "The authority transition did not continue after the exact permit was released.");
    AssertDecision(
        Observe(RuntimeAutomationControlTargetKind.Normal, RuntimeAutomationControlStage.FoodDelivery, now),
        false,
        "suspended-authority",
        "automation-primary-device-changing",
        21);

    using var denied = RuntimeAutomationControlState.AcquirePermit(
        RuntimeAutomationControlTargetKind.Normal,
        RuntimeAutomationControlStage.FoodDelivery,
        forceStageConfiguration: false,
        now);
    AssertTrue(!denied.Allowed, "A denied boundary returned an allowed permit.");
    var revoke = Task.Run(() => RuntimeAutomationControlState.RevokeLease("revoked", "revoked"));
    AssertTrue(revoke.Wait(TimeSpan.FromSeconds(2)), "A denied permit retained the control-state lock.");
    RuntimeAutomationControlState.Reset("test complete");
}

static void Publish(JsonElement profile, long revision, DateTime now)
{
    RuntimeAutomationControlState.PublishAuthority(profile, revision, "profile-changing", "profile changing");
    RuntimeAutomationControlState.PublishLease(revision, now.AddMinutes(1));
}

static RuntimeAutomationControlDecision Observe(
    RuntimeAutomationControlTargetKind target,
    RuntimeAutomationControlStage stage,
    DateTime now)
{
    return RuntimeAutomationControlState.Observe(target, stage, forceStageConfiguration: false, now);
}

static void AssertAllowed(
    RuntimeAutomationControlTargetKind target,
    RuntimeAutomationControlStage stage,
    DateTime now)
{
    var decision = Observe(target, stage, now);
    AssertDecision(decision, true, "active", "", decision.AuthorityRevision);
}

static void AssertDecision(
    RuntimeAutomationControlDecision decision,
    bool allowed,
    string state,
    string reasonCode,
    long authorityRevision)
{
    AssertTrue(
        decision.Allowed == allowed
        && string.Equals(decision.State, state, StringComparison.Ordinal)
        && string.Equals(decision.ReasonCode, reasonCode, StringComparison.Ordinal)
        && decision.AuthorityRevision == authorityRevision,
        $"Unexpected control decision: allowed={decision.Allowed}; state={decision.State}; "
        + $"reason={decision.ReasonCode}; revision={decision.AuthorityRevision}.");
}

static JsonElement Profile(
    bool automationEnabled = true,
    bool autoRareOrderEnabled = true,
    bool autoNormalOrderEnabled = true,
    bool autoPrepCollectCooking = true,
    bool autoPrepCompleteOrder = true,
    bool autoNormalDeliverFood = true,
    bool autoNormalCompleteOrder = true)
{
    return JsonSerializer.SerializeToElement(new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["automationEnabled"] = automationEnabled,
        ["autoRareOrderEnabled"] = autoRareOrderEnabled,
        ["autoNormalOrderEnabled"] = autoNormalOrderEnabled,
        ["autoPrepCollectCooking"] = autoPrepCollectCooking,
        ["autoPrepCompleteOrder"] = autoPrepCompleteOrder,
        ["autoNormalDeliverFood"] = autoNormalDeliverFood,
        ["autoNormalCompleteOrder"] = autoNormalCompleteOrder,
    });
}

static DateTime Utc(int hour, int minute, int second)
{
    return new DateTime(2026, 8, 10, hour, minute, second, DateTimeKind.Utc);
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
