using MystiaStewardCompanion.Save;
using System.Reflection;

try
{
    var tracker = new NightBusinessLifecycleTracker();
    var initial = tracker.Snapshot;
    AssertEqual(0L, initial.Generation, "Initial generation was not zero.");
    AssertEqual(0L, initial.Version, "Initial version was not zero.");
    AssertEqual(NightBusinessLifecyclePhase.Inactive, initial.Phase, "Initial phase was not Inactive.");

    var firstAt = new DateTime(2026, 7, 16, 1, 2, 3, DateTimeKind.Utc);
    AssertTrue(tracker.TryActivate("  panel ready  ", firstAt, 101, out var first), "First activation was rejected.");
    AssertEqual(1L, first.Generation, "First activation did not create generation 1.");
    AssertEqual(1L, first.Version, "First activation did not advance the version.");
    AssertEqual(NightBusinessLifecyclePhase.Active, first.Phase, "First activation did not enter Active.");
    AssertEqual("panel ready", first.Source, "Activation source was not normalized.");
    AssertEqual(firstAt, first.ChangedAtUtc, "Activation timestamp changed.");
    AssertEqual(101, first.ThreadId, "Activation thread ID changed.");
    AssertFalse(tracker.TryActivate("duplicate", firstAt.AddSeconds(1), 102, out var duplicateActive), "Duplicate activation was accepted.");
    AssertEqual(first, duplicateActive, "Duplicate activation changed the snapshot.");

    AssertRuntimeBoundaryContract();
    AssertRuntimeLifecycleCallbacks();

    AssertTrue(tracker.TryBeginClosing("close", firstAt.AddSeconds(2), 103, out var closing), "Active session did not enter Closing.");
    AssertEqual(first.Generation, closing.Generation, "Closing changed the business generation.");
    AssertEqual(first.Version + 1, closing.Version, "Closing did not advance the version once.");
    AssertEqual(NightBusinessLifecyclePhase.Closing, closing.Phase, "Closing transition used the wrong phase.");
    AssertFalse(tracker.TryBeginClosing("duplicate close", firstAt.AddSeconds(3), 104, out var duplicateClosing), "Duplicate Closing was accepted.");
    AssertEqual(closing, duplicateClosing, "Duplicate Closing changed the snapshot.");
    AssertFalse(tracker.TryActivate("early reopen", firstAt.AddSeconds(4), 105, out var earlyReopen), "Closing session reactivated before destruction.");
    AssertEqual(closing, earlyReopen, "Rejected reactivation changed the snapshot.");

    AssertTrue(tracker.TryMarkDestroyed("destroy", firstAt.AddSeconds(5), 106, out var destroyed), "Closing session did not enter Destroyed.");
    AssertEqual(first.Generation, destroyed.Generation, "Destroyed changed the business generation.");
    AssertEqual(closing.Version + 1, destroyed.Version, "Destroyed did not advance the version once.");
    AssertEqual(NightBusinessLifecyclePhase.Destroyed, destroyed.Phase, "Destroyed transition used the wrong phase.");
    AssertFalse(tracker.TryMarkDestroyed("duplicate destroy", firstAt.AddSeconds(6), 107, out var duplicateDestroyed), "Duplicate destruction was accepted.");
    AssertEqual(destroyed, duplicateDestroyed, "Duplicate destruction changed the snapshot.");

    AssertTrue(tracker.TryActivate("next panel", firstAt.AddSeconds(7), 108, out var second), "Destroyed session did not allow the next activation.");
    AssertEqual(first.Generation + 1, second.Generation, "Second activation did not advance the generation exactly once.");
    AssertEqual(destroyed.Version + 1, second.Version, "Second activation did not advance the version exactly once.");
    AssertEqual(NightBusinessLifecyclePhase.Active, second.Phase, "Second activation did not enter Active.");

    Console.WriteLine("PASS: night-business lifecycle preserves seated service after time expiry and keeps close/destroy transitions stable.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertTrue(bool actual, string message)
{
    if (!actual) throw new InvalidOperationException(message);
}

static void AssertFalse(bool actual, string message)
{
    if (actual) throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertRuntimeBoundaryContract()
{
    var assembly = typeof(NightBusinessLifecycleTracker).Assembly;
    using var stream = assembly.GetManifestResourceStream("RuntimeNightBusinessLifecycle.cs")
        ?? throw new InvalidOperationException("Runtime lifecycle source resource was not embedded.");
    using var reader = new StreamReader(stream);
    var source = reader.ReadToEnd();

    AssertTrue(source.Contains("private const int ExpectedHookCount = 5;", StringComparison.Ordinal),
        "Lifecycle hook count did not match the five verified runtime boundaries.");
    AssertFalse(source.Contains("\"TryCloseIzakaya\"", StringComparison.Ordinal),
        "Business-time expiry was incorrectly registered as a teardown boundary.");
    AssertFalse(source.Contains("OnNormalBusinessClosing", StringComparison.Ordinal),
        "The removed early-closing callback was retained.");
    foreach (var requiredBoundary in new[]
             {
                 "\"OnPannelPostOpen\"",
                 "\"CloseIzakayaDelayed\"",
                 "\"CloseIzakayaAndLeaveChallengeMode\"",
                 "\"ToResult\"",
                 "\"OnInstanceDestroyed\"",
             })
    {
        AssertTrue(source.Contains(requiredBoundary, StringComparison.Ordinal),
            $"Verified lifecycle boundary was missing: {requiredBoundary}.");
    }
}

static void AssertRuntimeLifecycleCallbacks()
{
    RuntimeBoundaryProbe.Reset();
    var runtimeType = typeof(RuntimeNightBusinessLifecycle);
    var hookStatus = runtimeType.GetField("_hookStatus", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Runtime hook status field was not found.");
    hookStatus.SetValue(null, "patched");

    InvokeRuntimeCallback(runtimeType, "OnBusinessStarted");
    var active = RuntimeNightBusinessLifecycle.Snapshot;
    AssertEqual(NightBusinessLifecyclePhase.Active, active.Phase, "Runtime did not enter Active at panel open.");
    AssertEqual(1L, active.Generation, "Runtime did not create generation 1.");
    AssertEqual(1, RuntimeBoundaryProbe.CookerResumeCount, "Cooker highlight did not resume at panel open.");
    AssertEqual(1, RuntimeBoundaryProbe.ListResumeCount, "List highlight did not resume at panel open.");
    AssertEqual(0, RuntimeBoundaryProbe.TargetInvalidationCount,
        "Panel open unexpectedly invalidated the active target.");
    AssertEqual(0, RuntimeBoundaryProbe.CookingJobClearCount,
        "Panel open unexpectedly cleared active cooking jobs.");

    AssertTrue(runtimeType.GetMethod("OnNormalBusinessClosing", BindingFlags.NonPublic | BindingFlags.Static) == null,
        "Business-time expiry callback still exists.");
    var draining = RuntimeNightBusinessLifecycle.Snapshot;
    AssertEqual(NightBusinessLifecyclePhase.Active, draining.Phase,
        "Business-time expiry should keep seated service Active.");
    AssertEqual(active.Generation, draining.Generation,
        "Business-time expiry changed the active generation.");
    AssertEqual(0, RuntimeBoundaryProbe.TargetInvalidationCount,
        "Business-time expiry invalidated the seated-service target.");
    AssertEqual(0, RuntimeBoundaryProbe.NormalOrderClearCount,
        "Business-time expiry cleared seated normal orders.");
    AssertEqual(0, RuntimeBoundaryProbe.CookingJobClearCount,
        "Business-time expiry cleared a seated-order cooking job.");

    InvokeRuntimeCallback(runtimeType, "OnDelayedBusinessClosing");
    var closing = RuntimeNightBusinessLifecycle.Snapshot;
    AssertEqual(NightBusinessLifecyclePhase.Closing, closing.Phase,
        "Final seated-guest drain did not enter Closing.");
    AssertEqual(active.Generation, closing.Generation, "Closing changed the active generation.");
    AssertEqual(1, RuntimeBoundaryProbe.TargetInvalidationCount, "Closing did not invalidate the UI target once.");
    AssertEqual(active.Generation, RuntimeBoundaryProbe.LastInvalidatedGeneration,
        "Closing invalidated the wrong generation.");
    AssertEqual(1, RuntimeBoundaryProbe.NormalOrderClearCount, "Closing did not clear normal orders once.");
    AssertEqual(1, RuntimeBoundaryProbe.SpecialOrderClearCount, "Closing did not clear special orders once.");
    AssertEqual(1, RuntimeBoundaryProbe.CookingJobClearCount, "Closing did not clear cooking jobs once.");

    InvokeRuntimeCallback(runtimeType, "OnResultTransitionStarting");
    AssertEqual(1, RuntimeBoundaryProbe.TargetInvalidationCount,
        "Duplicate result Closing repeated boundary cleanup.");
    AssertEqual(1, RuntimeBoundaryProbe.CookingJobClearCount,
        "Duplicate result Closing repeated cooking-job cleanup.");

    InvokeRuntimeCallback(runtimeType, "OnBusinessDestroyed");
    var destroyed = RuntimeNightBusinessLifecycle.Snapshot;
    AssertEqual(NightBusinessLifecyclePhase.Destroyed, destroyed.Phase,
        "Scene destruction did not enter Destroyed.");
    AssertEqual(1, RuntimeBoundaryProbe.CookerAbandonCount,
        "Scene destruction did not abandon cooker wrappers.");
    AssertEqual(1, RuntimeBoundaryProbe.ListAbandonCount,
        "Scene destruction did not abandon list wrappers.");

    InvokeRuntimeCallback(runtimeType, "OnBusinessStarted");
    var second = RuntimeNightBusinessLifecycle.Snapshot;
    AssertEqual(NightBusinessLifecyclePhase.Active, second.Phase,
        "Next panel open did not reactivate the runtime.");
    AssertEqual(active.Generation + 1, second.Generation,
        "Next panel open did not advance the generation.");
    InvokeRuntimeCallback(runtimeType, "OnChallengeBusinessClosing");
    AssertEqual(NightBusinessLifecyclePhase.Closing, RuntimeNightBusinessLifecycle.Snapshot.Phase,
        "Challenge teardown did not enter Closing.");
}

static void InvokeRuntimeCallback(Type runtimeType, string methodName)
{
    var method = runtimeType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Runtime callback was not found: {methodName}.");
    method.Invoke(null, null);
}
