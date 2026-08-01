using MystiaStewardCompanion.Save;

try
{
    VerifyHookReadinessAndInitialSnapshot();
    VerifyNestedMutationUsesOnlyShortBarrier();
    VerifySnapshotRaceAndGenerationIsolation();
    VerifyCanonicalSnapshotIdentity();
    VerifyProductionObserverContract();

    Console.WriteLine("PASS: Blood Pond Hell cooker topology leases fail closed and use short mutation barriers.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyHookReadinessAndInitialSnapshot()
{
    var tracker = new YuumaCookerTopologyTracker();
    AssertFalse(
        tracker.TryBeginSnapshot(1, hooksReady: false, out _, out _),
        "A partial Hook set allowed a topology read lease.");
    AssertTrue(
        tracker.TryBeginSnapshot(1, hooksReady: true, out var probe, out _),
        "A complete Hook set did not allow the initial fresh snapshot.");

    var snapshot = BuildIdentity(locked: false);
    AssertTrue(
        tracker.TryCommitSnapshot(
            probe,
            hooksReady: true,
            snapshot.Signature,
            snapshot.ControllerCount,
            snapshot.LockedControllerCount,
            out var lease,
            out _),
        "The initial complete snapshot did not produce a lease.");
    AssertTrue(
        tracker.TryBeginSnapshot(1, hooksReady: true, out var validationProbe, out _)
        && tracker.TryValidateSnapshot(
            validationProbe,
            hooksReady: true,
            lease,
            snapshot.Signature,
            snapshot.ControllerCount,
            snapshot.LockedControllerCount,
            out _),
        "A fresh identical snapshot did not validate its lease.");
    AssertFalse(
        tracker.TryBeginSnapshot(1, hooksReady: false, out _, out _),
        "Hook readiness loss did not fail closed.");

    var skipped = tracker.BeginMutation(1, "skipped-by-another-prefix");
    AssertFalse(
        tracker.TryBeginSnapshot(1, hooksReady: true, out _, out _),
        "A skipped original did not hold its prefix/postfix short barrier.");
    AssertTrue(
        tracker.CompleteMutation(skipped, originalRan: false),
        "A skipped original did not release its short barrier.");
    AssertTrue(
        Validate(tracker, lease, snapshot),
        "A skipped original incorrectly advanced the topology revision.");
}

static void VerifyNestedMutationUsesOnlyShortBarrier()
{
    var tracker = new YuumaCookerTopologyTracker();
    var initial = Acquire(tracker, generation: 7, BuildIdentity(locked: false));
    var outer = tracker.BeginMutation(7, "EventManager.LockCookers_Forever");
    var inner = tracker.BeginMutation(7, "PartnerManager.OnCookerAvailabilityUpdate(-1)");
    AssertEqual(2, tracker.MutationDepth, "Nested public callbacks did not create depth two.");
    AssertFalse(
        tracker.TryBeginSnapshot(7, hooksReady: true, out _, out _),
        "A snapshot started inside the nested topology mutation.");

    AssertTrue(tracker.CompleteMutation(inner, originalRan: true), "The inner callback did not complete.");
    AssertEqual(1, tracker.MutationDepth, "Completing the inner callback released the outer barrier.");
    AssertFalse(
        tracker.TryBeginSnapshot(7, hooksReady: true, out _, out _),
        "A snapshot started before the outer public method returned.");
    AssertTrue(tracker.CompleteMutation(outer, originalRan: true), "The outer callback did not complete.");
    AssertEqual(0, tracker.MutationDepth, "The short public-method barrier remained active.");

    var permanentlyLockedSnapshot = BuildIdentity(locked: true);
    var current = Acquire(tracker, 7, permanentlyLockedSnapshot);
    AssertEqual(1, current.LockedControllerCount, "The permanent lock was not represented by snapshot identity.");
    AssertFalse(
        Validate(tracker, initial, permanentlyLockedSnapshot),
        "A lease from before the permanent lock remained valid.");
    AssertTrue(
        Validate(tracker, current, permanentlyLockedSnapshot),
        "A fresh post-return snapshot could not resume work on remaining cookers.");
}

static void VerifySnapshotRaceAndGenerationIsolation()
{
    var tracker = new YuumaCookerTopologyTracker();
    AssertTrue(
        tracker.TryBeginSnapshot(11, hooksReady: true, out var staleProbe, out _),
        "The initial read probe was rejected.");
    var mutation = tracker.BeginMutation(11, "availability");
    AssertTrue(tracker.CompleteMutation(mutation, originalRan: true), "The mutation did not complete.");
    var snapshot = BuildIdentity(locked: false);
    AssertFalse(
        tracker.TryCommitSnapshot(
            staleProbe,
            hooksReady: true,
            snapshot.Signature,
            snapshot.ControllerCount,
            snapshot.LockedControllerCount,
            out _,
            out _),
        "A snapshot read across a topology mutation was acknowledged.");

    var current = Acquire(tracker, 11, snapshot);
    var neverCompleted = tracker.BeginMutation(11, "native method threw");
    AssertFalse(
        tracker.TryBeginSnapshot(11, hooksReady: true, out _, out _),
        "A missing postfix did not remain fail closed.");
    tracker.Reset();
    AssertFalse(
        tracker.CompleteMutation(neverCompleted, originalRan: true),
        "A stale postfix changed the reset tracker.");
    var next = Acquire(tracker, 11, snapshot);
    AssertFalse(Validate(tracker, current, snapshot), "Reset did not invalidate a same-generation stale lease.");
    AssertTrue(Validate(tracker, next, snapshot), "Reset prevented a fresh lease in the new epoch.");

    var nextGeneration = Acquire(tracker, 12, snapshot);
    AssertFalse(Validate(tracker, next, snapshot), "The previous business generation remained valid.");
    AssertTrue(Validate(tracker, nextGeneration, snapshot), "The next business generation lease was rejected.");
}

static void VerifyCanonicalSnapshotIdentity()
{
    var unlocked = BuildIdentity(locked: false);
    var unlockedAgain = BuildIdentity(locked: false);
    var locked = BuildIdentity(locked: true);
    AssertEqual(unlocked.Signature, unlockedAgain.Signature, "Identical physical directories changed signature.");
    AssertFalse(
        string.Equals(unlocked.Signature, locked.Signature, StringComparison.Ordinal),
        "A permanent lock did not change the canonical signature.");

    var replacedControllers = BuildControllers().ToArray();
    replacedControllers[1] = replacedControllers[1] with { ControllerIdentity = "0x3000" };
    AssertTrue(
        YuumaCookerTopologySnapshotIdentityBuilder.TryCreate(
            replacedControllers,
            Array.Empty<YuumaCookerTopologyPosition>(),
            out var replaced,
            out _),
        "A valid replacement directory was rejected.");
    AssertFalse(
        string.Equals(unlocked.Signature, replaced.Signature, StringComparison.Ordinal),
        "Controller replacement did not change the canonical signature.");

    var tracker = new YuumaCookerTopologyTracker();
    var lease = Acquire(tracker, 21, unlocked);
    AssertFalse(
        Validate(tracker, lease, replaced),
        "A fresh unhooked topology drift did not reject the current lease.");
    AssertFalse(
        Validate(tracker, lease, unlocked),
        "A rejected fresh drift did not permanently invalidate the old lease.");
}

static YuumaCookerTopologyLease Acquire(
    YuumaCookerTopologyTracker tracker,
    long generation,
    YuumaCookerTopologySnapshotIdentity snapshot)
{
    AssertTrue(
        tracker.TryBeginSnapshot(generation, hooksReady: true, out var probe, out var beginDiagnostic),
        $"Could not begin a fresh snapshot: {beginDiagnostic}");
    AssertTrue(
        tracker.TryCommitSnapshot(
            probe,
            hooksReady: true,
            snapshot.Signature,
            snapshot.ControllerCount,
            snapshot.LockedControllerCount,
            out var lease,
            out var commitDiagnostic),
        $"Could not acknowledge a fresh snapshot: {commitDiagnostic}");
    return lease;
}

static bool Validate(
    YuumaCookerTopologyTracker tracker,
    YuumaCookerTopologyLease lease,
    YuumaCookerTopologySnapshotIdentity snapshot)
{
    return tracker.TryBeginSnapshot(
            lease.BusinessGeneration,
            hooksReady: true,
            out var probe,
            out _)
        && tracker.TryValidateSnapshot(
            probe,
            hooksReady: true,
            lease,
            snapshot.Signature,
            snapshot.ControllerCount,
            snapshot.LockedControllerCount,
            out _);
}

static YuumaCookerTopologySnapshotIdentity BuildIdentity(bool locked)
{
    var lockedPositions = locked
        ? new[] { new YuumaCookerTopologyPosition(1, 0, 0) }
        : Array.Empty<YuumaCookerTopologyPosition>();
    AssertTrue(
        YuumaCookerTopologySnapshotIdentityBuilder.TryCreate(
            BuildControllers(),
            lockedPositions,
            out var snapshot,
            out var diagnostic),
        $"Could not build a canonical snapshot identity: {diagnostic}");
    return snapshot;
}

static IReadOnlyList<YuumaCookerTopologyControllerIdentity> BuildControllers()
{
    return new[]
    {
        new YuumaCookerTopologyControllerIdentity(0, "0x1000", new YuumaCookerTopologyPosition(0, 0, 0)),
        new YuumaCookerTopologyControllerIdentity(1, "0x2000", new YuumaCookerTopologyPosition(1, 0, 0)),
    };
}

static void VerifyProductionObserverContract()
{
    var root = FindRepositoryRoot();
    var source = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "SpecialBusiness",
        "YuumaCookerTopologyObserver.cs"));
    var normalized = Normalize(source);
    AssertContains(
        normalized,
        "private const int ExpectedHookCount = 3;",
        "The topology observer no longer requires exactly three public runtime hooks.");
    AssertContains(
        normalized,
        "GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)",
        "The topology observer can resolve inherited, non-public, or static methods.");

    foreach (var required in new[]
             {
                 "EventManagerTypeName, \"LockCookers\"",
                 "IsClosedGeneric(Il2CppEnumerableTypeName, IsInt32), IsInt32, IsByRef(IsExact(Il2CppActionTypeName)), IsExact(Il2CppActionTypeName), IsExact(BuffTypeName)",
                 "nameof(OnLockCookersPrefix), nameof(OnLockCookersPostfix)",
                 "EventManagerTypeName, \"LockCookers_Forever\"",
                 "IsClosedGeneric(Il2CppEnumerableTypeName, IsInt32), IsByRef(IsExact(Il2CppActionTypeName)), IsExact(Il2CppActionTypeName)",
                 "nameof(OnLockCookersForeverPrefix), nameof(OnLockCookersForeverPostfix)",
                 "PartnerManagerTypeName, \"OnCookerAvailabilityUpdate\"",
                 "method => MatchesMethod(method, IsVoid, IsInt32)",
                 "nameof(OnCookerAvailabilityUpdatePrefix), nameof(OnCookerAvailabilityUpdatePostfix)",
             })
    {
        AssertContains(normalized, required, $"Missing exact topology Hook contract: {required}");
    }

    foreach (var methodName in new[]
             {
                 "OnLockCookersPrefix",
                 "OnLockCookersForeverPrefix",
                 "OnCookerAvailabilityUpdatePrefix",
             })
    {
        var prefix = Normalize(ExtractMethod(source, $"private static void {methodName}("));
        AssertContains(prefix, "out YuumaCookerTopologyMutationFrame? __state", $"{methodName} does not publish an exact callback frame.");
        AssertContains(prefix, "__state = BeginMutation(source);", $"{methodName} no longer enters the generation-scoped tracker first.");
        AssertContains(prefix, "if (__state != null) RuntimeCookerHighlightService.BeginTopologyMutation(source);", $"{methodName} can abandon renderer wrappers outside an active Blood Pond Hell frame.");
        AssertDoesNotContain(prefix, "IEnumerable", $"{methodName} reads or rewrites the native cooker sequence.");
        AssertDoesNotContain(prefix, "Action", $"{methodName} reads or rewrites a native completion callback.");
    }

    foreach (var methodName in new[]
             {
                 "OnLockCookersPostfix",
                 "OnLockCookersForeverPostfix",
                 "OnCookerAvailabilityUpdatePostfix",
             })
    {
        var postfix = Normalize(ExtractMethod(source, $"private static void {methodName}("));
        AssertContains(postfix, "bool __runOriginal", $"{methodName} lost the Harmony original-run result.");
        AssertContains(postfix, "CompleteMutation(__state, __runOriginal);", $"{methodName} no longer completes the short tracker barrier.");
        AssertContains(postfix, "if (__state != null)", $"{methodName} can complete a highlighter barrier that was never entered.");
        AssertContains(postfix, "RuntimeCookerHighlightService.CompleteTopologyMutation(", $"{methodName} no longer releases the renderer barrier.");
    }

    AssertDoesNotContain(source, "MoveNext", "The topology observer hooks a compiler-generated coroutine method.");
    AssertDoesNotContain(source, "GetEnumerator", "The topology observer enumerates the native cooker target sequence.");
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "package.json"))
            && Directory.Exists(Path.Combine(current.FullName, "mods", "bepinex")))
        {
            return current.FullName;
        }
        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Repository root was not found.");
}

static string ExtractMethod(string source, string signature)
{
    var start = source.IndexOf(signature, StringComparison.Ordinal);
    if (start < 0) throw new InvalidOperationException($"Method signature not found: {signature}");
    var bodyStart = source.IndexOf('{', start);
    if (bodyStart < 0) throw new InvalidOperationException($"Method body not found: {signature}");
    var depth = 0;
    for (var index = bodyStart; index < source.Length; index++)
    {
        if (source[index] == '{') depth++;
        else if (source[index] == '}' && --depth == 0) return source[start..(index + 1)];
    }
    throw new InvalidOperationException($"Method body is incomplete: {signature}");
}

static string Normalize(string value)
{
    return System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
}

static void AssertContains(string actual, string expected, string message)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Missing: {expected}");
    }
}

static void AssertDoesNotContain(string actual, string forbidden, string message)
{
    if (actual.Contains(forbidden, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Forbidden: {forbidden}");
    }
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
