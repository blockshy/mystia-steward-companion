using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;

try
{
    VerifyControllerFailureFailsClosed();
    VerifyUnavailableSourceClearsPriorSnapshot();
    VerifyCompleteSnapshot();
    VerifyExactEmptyControllers();
    VerifyAllExactEmptyControllers();
    VerifyLaterFailureClearsExactEmptyFacts();
    VerifyChallengeLockedEmptyController();
    VerifyCompleteEmptySnapshot();
    VerifyBoundedControllerFailureDiagnostics();
    VerifyBoundedAvailabilityDiagnostic();
    VerifyChallengeLockProjection();
    VerifyChallengeGateMismatchFailsClosed();
    VerifyExactReservationIdentity();
    VerifyCookerContentSignature();
    VerifyLockedCookerSourceContracts();

    Console.WriteLine("PASS: runtime cooker snapshots retain exact identities and fail closed on uncertain lock state.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyControllerFailureFailsClosed()
{
    var locked = new object();
    var unreadable = new object();
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(locked, unreadable),
        "allCookers=ok; entries=2; controllers=2");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition> { new(0, 0, 0) },
        "lockedCookers=ok; entries=1; unique=1");
    RuntimeCookingGenerationTracker.OwnershipReader = _ => (
        false,
        default,
        "ownership=unavailable");
    RuntimeCookerReflection.StateReader = controller =>
    {
        if (ReferenceEquals(controller, locked))
        {
            throw new InvalidOperationException("Locked controller state must never be read.");
        }

        return (
            false,
            new RuntimeCookerControllerState(),
            "controller-state=cooker-types-unavailable\nignored-line");
    };

    var state = BuildStaleState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(false, state.PlacedCookerSnapshotComplete, "A failed snapshot was marked complete.");
    AssertEqual(2, state.PlacedCookerControllerCount, "The source controller count was not retained.");
    AssertEqual(0, state.PlacedCookerEmptyControllerCount, "A failed round retained partial empty-controller facts.");
    AssertEqual(1, state.PlacedCookerLockedControllerCount, "The failed round lost the safely classified locked controller.");
    AssertEqual(1, state.PlacedCookerReadFailureCount, "The failed round did not classify the unreadable unlocked controller.");
    AssertEqual(0, state.PlacedCookers.Count, "A failed round retained a partially readable controller.");
    AssertEqual(0, state.PlacedCookerTypeIds.Count, "A failed round retained partially readable types.");
    AssertContains(state.PlacedCookerStatus, "source unavailable;", "The fail-closed status is missing.");
    AssertContains(state.PlacedCookerStatus, "controller=1/0x1001", "The failed exact unlocked controller identity is missing.");
    AssertContains(state.PlacedCookerStatus, "controller-state=cooker-types-unavailable ignored-line", "The bounded failure detail is missing.");

    var dto = RecommendationStateSnapshot.From(state);
    AssertEqual(false, dto.PlacedCookerSnapshotComplete, "The API DTO changed fail-closed completeness.");
    AssertEqual(2, dto.PlacedCookerControllerCount, "The API DTO dropped the source controller count.");
    AssertEqual(0, dto.PlacedCookerEmptyControllerCount, "The API DTO restored partial empty-controller facts.");
    AssertEqual(1, dto.PlacedCookerLockedControllerCount, "The API DTO dropped the authoritative locked count.");
    AssertEqual(1, dto.PlacedCookerReadFailureCount, "The API DTO dropped the fail-closed failure count.");
    AssertEqual(0, dto.PlacedCookers.Count, "The API DTO restored partial cooker entries.");
}

static void VerifyUnavailableSourceClearsPriorSnapshot()
{
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        false,
        Array.Empty<RuntimeCookerControllerEntry>(),
        "allCookers=read-failed; failure=test");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition>(),
        "lockedCookers=ok; entries=0; unique=0");

    var state = BuildStaleState();
    state.PlacedCookerSnapshotComplete = true;
    state.PlacedCookerControllerCount = 1;
    state.PlacedCookerEmptyControllerCount = 1;
    state.PlacedCookerLockedControllerCount = 1;
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(false, state.PlacedCookerSnapshotComplete, "An unavailable source was marked complete.");
    AssertEqual(0, state.PlacedCookerControllerCount, "An unavailable source retained a stale controller count.");
    AssertEqual(0, state.PlacedCookerEmptyControllerCount, "An unavailable source retained stale empty-controller facts.");
    AssertEqual(0, state.PlacedCookerLockedControllerCount, "An unavailable source retained stale locked-controller facts.");
    AssertEqual(0, state.PlacedCookerReadFailureCount, "A source failure was misclassified as a controller failure.");
    AssertEqual(0, state.PlacedCookers.Count, "An unavailable source retained stale cooker entries.");
    AssertEqual(0, state.PlacedCookerTypeIds.Count, "An unavailable source retained stale cooker types.");
    AssertContains(state.PlacedCookerStatus, "source unavailable;", "The unavailable status is missing.");
}

static void VerifyCompleteSnapshot()
{
    var first = new object();
    var second = new object();
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(first, second),
        "allCookers=ok; entries=2; controllers=2");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition>(),
        "lockedCookers=ok; entries=0; unique=0");
    RuntimeCookerReflection.StateReader = controller =>
    {
        if (ReferenceEquals(controller, first))
        {
            return (
                true,
                BuildControllerState(typeIds: new[] { 2 }, couldOpen: true, phase: 1),
                "controller-state=ok");
        }

        return (
            true,
            BuildControllerState(
                typeIds: new[] { 5 },
                couldOpen: true,
                chosenRecipe: new object()),
            "controller-state=ok");
    };
    RuntimeCookingGenerationTracker.OwnershipReader = controller => ReferenceEquals(controller, second)
        ? (
            true,
            new RuntimeCookingOwnershipSnapshot(RuntimeCookingContentMutation.Extract, MutationCompleted: true),
            "ownership=completed-extract")
        : (false, default, "ownership=unavailable");

    var state = new RecommendationState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(true, state.PlacedCookerSnapshotComplete, "A fully readable source was not marked complete.");
    AssertEqual(2, state.PlacedCookerControllerCount, "The complete controller count is incorrect.");
    AssertEqual(0, state.PlacedCookerEmptyControllerCount, "A complete placed-only snapshot reported empty controllers.");
    AssertEqual(0, state.PlacedCookerLockedControllerCount, "A complete unlocked snapshot reported locked controllers.");
    AssertEqual(0, state.PlacedCookerReadFailureCount, "A complete source reported controller failures.");
    AssertEqual(2, state.PlacedCookers.Count, "A complete source lost readable controllers.");
    AssertEqual("0x1000", state.PlacedCookers[0].ControllerIdentity, "The first native identity changed.");
    AssertEqual(0, state.PlacedCookers[0].GridPosition.X, "The first dictionary position changed.");
    AssertEqual(false, state.PlacedCookers[0].ChallengeLocked, "An unlocked cooker was marked challenge-locked.");
    AssertEqual(true, state.PlacedCookers[0].CouldOpen, "The busy cooker native open gate changed.");
    AssertEqual(false, state.PlacedCookers[0].AutomationAvailable, "A busy cooker became automation-available.");
    AssertEqual("Unavailable", state.PlacedCookers[0].AutomationAvailability, "The busy cooker diagnostic changed.");
    AssertEqual(true, state.PlacedCookers[1].CouldOpen, "The extracted-residual native open gate changed.");
    AssertEqual(true, state.PlacedCookers[1].AutomationAvailable, "A completed Extract residual was not reusable.");
    AssertEqual("ExtractedResidual", state.PlacedCookers[1].AutomationAvailability, "The Extract residual diagnostic changed.");
    AssertEqual(
        "startAvailability=ExtractedResidual; ownership=completed-extract",
        state.PlacedCookers[1].AutomationAvailabilityDiagnostic,
        "The Extract residual detail diagnostic changed.");
    AssertContains(state.PlacedCookerStatus, "complete;", "Complete status is missing.");
}

static void VerifyExactEmptyControllers()
{
    var first = new object();
    var empty = new object();
    var third = new object();
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(first, empty, third),
        "allCookers=ok; entries=3; controllers=3");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition>(),
        "lockedCookers=ok; entries=0; unique=0");
    RuntimeCookerReflection.StateReader = controller =>
    {
        if (ReferenceEquals(controller, empty))
        {
            return (
                true,
                BuildControllerState(
                    typeIds: Array.Empty<int>(),
                    couldOpen: true,
                    isEmptyDesk: true),
                "controller-state=ok-empty-desk");
        }

        return (
            true,
            BuildControllerState(
                typeIds: new[] { ReferenceEquals(controller, first) ? 1 : 2 },
                couldOpen: true),
            "controller-state=ok");
    };
    RuntimeCookingGenerationTracker.OwnershipReader = _ => (
        false,
        default,
        "ownership=unavailable");

    var state = new RecommendationState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(true, state.PlacedCookerSnapshotComplete, "An exact empty desk made the snapshot unavailable.");
    AssertEqual(3, state.PlacedCookerControllerCount, "The source controller total dropped the empty desk.");
    AssertEqual(1, state.PlacedCookerEmptyControllerCount, "The exact empty desk count was not retained.");
    AssertEqual(0, state.PlacedCookerLockedControllerCount, "An unlocked empty desk was classified as locked.");
    AssertEqual(0, state.PlacedCookerReadFailureCount, "An exact empty desk was classified as a read failure.");
    AssertEqual(2, state.PlacedCookers.Count, "An exact empty desk was published as a placed cooker.");
    AssertEqual(0, state.PlacedCookers[0].ControllerIndex, "The first placed controller index changed.");
    AssertEqual(2, state.PlacedCookers[1].ControllerIndex, "The controller index after an empty desk was compacted.");
    AssertEqual("1,2", string.Join(",", state.PlacedCookerTypeIds.OrderBy(id => id)),
        "The empty desk changed the physical cooker type projection.");
    AssertContains(state.PlacedCookerStatus, "empty=1", "The complete status omitted the exact empty desk count.");

    var emptyState = BuildControllerState(
        typeIds: Array.Empty<int>(),
        couldOpen: true,
        isEmptyDesk: true);
    var emptyAvailability = RuntimeCookerStartAvailabilityService.Classify(
        empty,
        emptyState,
        out var emptyDiagnostic);
    AssertEqual(AutomationCookerStartAvailability.Unavailable, emptyAvailability,
        "An exact empty desk became automation capacity.");
    AssertContains(emptyDiagnostic, "emptyDesk=True", "The empty-desk availability diagnostic is missing.");

    var dto = RecommendationStateSnapshot.From(state);
    AssertEqual(1, dto.PlacedCookerEmptyControllerCount, "The API DTO dropped the exact empty desk count.");
    AssertEqual(2, dto.PlacedCookers.Count, "The API DTO published an empty desk as a placed cooker.");
}

static void VerifyAllExactEmptyControllers()
{
    var controllers = new[] { new object(), new object() };
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(controllers),
        "allCookers=ok; entries=2; controllers=2");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition>(),
        "lockedCookers=ok; entries=0; unique=0");
    RuntimeCookerReflection.StateReader = _ => (
        true,
        BuildControllerState(
            typeIds: Array.Empty<int>(),
            couldOpen: true,
            isEmptyDesk: true),
        "controller-state=ok-empty-desk");

    var state = BuildStaleState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(true, state.PlacedCookerSnapshotComplete, "An all-empty controller source was not complete.");
    AssertEqual(2, state.PlacedCookerControllerCount, "An all-empty source lost its controller total.");
    AssertEqual(2, state.PlacedCookerEmptyControllerCount, "An all-empty source lost exact empty desks.");
    AssertEqual(0, state.PlacedCookerLockedControllerCount, "An all-empty unlocked source reported locked controllers.");
    AssertEqual(0, state.PlacedCookerReadFailureCount, "Exact empty desks became read failures.");
    AssertEqual(0, state.PlacedCookers.Count, "An exact empty desk was published as a placed cooker.");
    AssertEqual(0, state.PlacedCookerTypeIds.Count, "An exact empty desk created physical cooker types.");
}

static void VerifyLaterFailureClearsExactEmptyFacts()
{
    var empty = new object();
    var unreadable = new object();
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(empty, unreadable),
        "allCookers=ok; entries=2; controllers=2");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition>(),
        "lockedCookers=ok; entries=0; unique=0");
    RuntimeCookerReflection.StateReader = controller => ReferenceEquals(controller, empty)
        ? (
            true,
            BuildControllerState(
                typeIds: Array.Empty<int>(),
                couldOpen: true,
                isEmptyDesk: true),
            "controller-state=ok-empty-desk")
        : (
            false,
            new RuntimeCookerControllerState(),
            "controller-state=current-invoke-failed");

    var state = BuildStaleState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(false, state.PlacedCookerSnapshotComplete, "A later unreadable controller left a complete snapshot.");
    AssertEqual(2, state.PlacedCookerControllerCount, "A failed round lost the source controller total.");
    AssertEqual(0, state.PlacedCookerEmptyControllerCount, "A failed round retained a partial empty count.");
    AssertEqual(0, state.PlacedCookerLockedControllerCount, "A failed unlocked round reported locked controllers.");
    AssertEqual(2, state.PlacedCookerReadFailureCount, "A failed round did not invalidate all controllers.");
    AssertEqual(0, state.PlacedCookers.Count, "A failed round retained partial placed cookers.");
    AssertEqual(0, state.PlacedCookerTypeIds.Count, "A failed round retained partial cooker types.");
}

static void VerifyChallengeLockedEmptyController()
{
    var locked = new object();
    var stateReadCount = 0;
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(locked),
        "allCookers=ok; entries=1; controllers=1");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition> { new(0, 0, 0) },
        "lockedCookers=ok; entries=1; unique=1");
    RuntimeCookerReflection.StateReader = _ =>
    {
        stateReadCount++;
        throw new InvalidOperationException("Locked controller state must never be read.");
    };

    var state = new RecommendationState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(true, state.PlacedCookerSnapshotComplete, "A dictionary-confirmed locked controller failed the snapshot.");
    AssertEqual(0, stateReadCount, "The locked controller state getter was invoked.");
    AssertEqual(0, state.PlacedCookerEmptyControllerCount, "A locked controller was guessed to be an empty desk.");
    AssertEqual(1, state.PlacedCookerLockedControllerCount, "The locked controller was not counted authoritatively.");
    AssertEqual(0, state.PlacedCookerReadFailureCount, "A safely skipped locked controller became a read failure.");
    AssertEqual(0, state.PlacedCookers.Count, "A locked controller created placed cooker capacity.");
    AssertEqual(0, state.PlacedCookerTypeIds.Count, "A locked controller leaked guessed cooker types.");
}

static void VerifyCompleteEmptySnapshot()
{
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        Array.Empty<RuntimeCookerControllerEntry>(),
        "allCookers=ok; entries=0; controllers=0");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition>(),
        "lockedCookers=ok; entries=0; unique=0");

    var state = BuildStaleState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(true, state.PlacedCookerSnapshotComplete, "A successfully read empty dictionary was not complete.");
    AssertEqual(0, state.PlacedCookerControllerCount, "An empty dictionary reported controllers.");
    AssertEqual(0, state.PlacedCookerEmptyControllerCount, "An empty dictionary reported empty controllers.");
    AssertEqual(0, state.PlacedCookerLockedControllerCount, "An empty dictionary reported locked controllers.");
    AssertEqual(0, state.PlacedCookerReadFailureCount, "An empty dictionary reported failures.");
    AssertEqual(0, state.PlacedCookers.Count, "An empty dictionary retained stale cooker entries.");
    AssertEqual(0, state.PlacedCookerTypeIds.Count, "An empty dictionary retained stale cooker types.");
}

static void VerifyBoundedControllerFailureDiagnostics()
{
    var controllers = Enumerable.Range(0, 5).Select(_ => new object()).ToArray();
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(controllers),
        "allCookers=ok; entries=5; controllers=5");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition>(),
        "lockedCookers=ok; entries=0; unique=0");
    RuntimeCookerReflection.StateReader = _ => (
        false,
        new RuntimeCookerControllerState(),
        "controller-state=failed;" + new string('x', 400));

    var state = new RecommendationState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(5, state.PlacedCookerReadFailureCount, "The full failed controller count was truncated.");
    AssertEqual(5, state.PlacedCookerReadFailureCount, "The failed round did not invalidate every controller.");
    AssertEqual(0, state.PlacedCookers.Count, "A failed round retained partial entries.");
    AssertContains(state.PlacedCookerStatus, "controller=0/0x1000", "The first exact failure identity is missing.");
    AssertDoesNotContain(state.PlacedCookerStatus, new string('x', 200), "The controller diagnostic was not bounded.");
    AssertEqual(true, state.PlacedCookerStatus.Length < 400, "Bounded controller diagnostics grew unexpectedly large.");
}

static void VerifyBoundedAvailabilityDiagnostic()
{
    var controller = new object();
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(controller),
        "allCookers=ok; entries=1; controllers=1");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition>(),
        "lockedCookers=ok; entries=0; unique=0");
    RuntimeCookerReflection.StateReader = _ => (
        true,
        BuildControllerState(
            typeIds: new[] { 4 },
            couldOpen: true,
            chosenRecipe: new object()),
        "controller-state=ok");
    RuntimeCookingGenerationTracker.OwnershipReader = _ => (
        false,
        default,
        "ownership=failed\n" + new string('x', 400));

    var state = new RecommendationState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    var cooker = state.PlacedCookers.Single();
    AssertEqual(false, cooker.AutomationAvailable, "Unreadable residual ownership created automation capacity.");
    AssertEqual("Unavailable", cooker.AutomationAvailability, "Unreadable residual ownership changed the stable state.");
    AssertEqual(false, cooker.AutomationAvailabilityDiagnostic.Contains('\n'), "The availability diagnostic contains a newline.");
    AssertEqual(true, cooker.AutomationAvailabilityDiagnostic.Length <= 243, "The availability diagnostic exceeded its fixed bound.");
    AssertContains(cooker.AutomationAvailabilityDiagnostic, "startAvailability=Unavailable;", "The availability detail lost its classification.");
}

static void VerifyChallengeLockProjection()
{
    var open = new object();
    var locked = new object();
    var lockedStateReadCount = 0;
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(open, locked),
        "allCookers=ok; entries=2; controllers=2");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition> { new(1, 0, 0) },
        "lockedCookers=ok; entries=1; unique=1");
    RuntimeCookerReflection.StateReader = controller =>
    {
        if (ReferenceEquals(controller, locked))
        {
            lockedStateReadCount++;
            throw new InvalidOperationException("Locked controller state must never be read.");
        }

        return (
            true,
            BuildControllerState(typeIds: new[] { 2 }, couldOpen: true),
            "controller-state=ok");
    };
    RuntimeCookingGenerationTracker.OwnershipReader = _ => (
        false,
        default,
        "ownership=unavailable");

    var state = new RecommendationState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(true, state.PlacedCookerSnapshotComplete, "An exact lock snapshot was not complete.");
    AssertEqual(0, lockedStateReadCount, "The exact locked controller was dereferenced.");
    AssertEqual(2, state.PlacedCookerControllerCount, "The exact lock snapshot lost the directory total.");
    AssertEqual(1, state.PlacedCookerLockedControllerCount, "The exact locked position was not counted.");
    AssertEqual(0, state.PlacedCookerEmptyControllerCount, "The locked controller was guessed to be empty.");
    AssertEqual(0, state.PlacedCookerReadFailureCount, "The safely skipped locked controller became a failure.");
    AssertEqual(1, state.PlacedCookers.Count, "A locked controller was projected as available cooker capacity.");
    AssertEqual(false, state.PlacedCookers[0].ChallengeLocked, "The open position was marked locked.");
    AssertEqual(true, state.PlacedCookers[0].CouldOpen, "The open native gate changed.");
    AssertEqual(0, state.PlacedCookers[0].GridPosition.X, "The open dictionary position changed.");
    AssertEqual("0x1000", state.PlacedCookers[0].ControllerIdentity, "The open controller identity changed.");
    AssertEqual("2", string.Join(",", state.PlacedCookerTypeIds), "The locked controller changed usable type capacity.");

    var dto = RecommendationStateSnapshot.From(state);
    AssertEqual(1, dto.PlacedCookerLockedControllerCount, "The API DTO dropped the authoritative locked count.");
    AssertEqual(1, dto.PlacedCookers.Count, "The API DTO restored a locked cooker projection.");
    AssertEqual("0x1000", dto.PlacedCookers[0].ControllerIdentity, "The API DTO changed the open controller identity.");
}

static void VerifyChallengeGateMismatchFailsClosed()
{
    var controller = new object();
    RuntimeCookerReflection.ManagerReader = () => new object();
    RuntimeCookerReflection.ControllerEntryReader = _ => (
        true,
        BuildControllerEntries(controller),
        "allCookers=ok; entries=1; controllers=1");
    RuntimeCookerReflection.LockedPositionReader = () => (
        true,
        new HashSet<RuntimeCookerGridPosition>(),
        "lockedCookers=ok; entries=0; unique=0");
    RuntimeCookerReflection.StateReader = _ => (
        true,
        BuildControllerState(typeIds: new[] { 3 }, couldOpen: false),
        "controller-state=ok");

    var state = BuildStaleState();
    RuntimeCookerSnapshotService.ApplyTo(state);

    AssertEqual(false, state.PlacedCookerSnapshotComplete, "An unlocked but natively closed gate was marked complete.");
    AssertEqual(0, state.PlacedCookers.Count, "A contradictory lock gate retained stale entries.");
    AssertEqual(0, state.PlacedCookerLockedControllerCount, "A missing LockedCookers entry was guessed as locked.");
    AssertEqual(1, state.PlacedCookerReadFailureCount, "A contradictory lock gate did not fail the round.");
    AssertContains(state.PlacedCookerStatus, "gate-mismatch", "The exact lock contradiction is not diagnostic.");
}

static void VerifyExactReservationIdentity()
{
    AssertEqual(
        false,
        RuntimeCookerReservation.TryCreate(
            1,
            "",
            1,
            0,
            0,
            out _,
            out _),
        "A missing controller identity created an action reservation.");
    AssertEqual(
        false,
        RuntimeCookerReservation.TryCreate(
            1,
            "0x0",
            1,
            0,
            0,
            out _,
            out _),
        "A zero native identity created an action reservation.");
    AssertEqual(
        false,
        RuntimeCookerReservation.TryCreate(
            1,
            "0x1001",
            1,
            null,
            0,
            out _,
            out _),
        "An incomplete grid position created an action reservation.");
    AssertEqual(
        true,
        RuntimeCookerReservation.TryCreate(
            1,
            "0x1001",
            1,
            0,
            0,
            out var reservation,
            out var createError),
        $"An exact action reservation was rejected: {createError}");

    var initial = BuildControllerEntries(new object(), new object());
    AssertEqual(
        true,
        reservation.TryMatch(initial, out var initialEntry, out var initialError),
        $"The unchanged exact controller was rejected: {initialError}");
    AssertEqual("0x1001", initialEntry.ControllerIdentity, "The reservation selected a different controller identity.");

    var removedEarlierCoordinate = new[]
    {
        new RuntimeCookerControllerEntry
        {
            Controller = new object(),
            GridPosition = new RuntimeCookerGridPosition(1, 0, 0),
            ControllerIdentity = "0x1001",
        },
    };
    AssertEqual(
        false,
        reservation.TryMatch(removedEarlierCoordinate, out _, out _),
        "Removing an earlier coordinate silently shifted the reserved index.");

    var pointerReplacement = initial
        .Select((entry, index) => index == 1
            ? new RuntimeCookerControllerEntry
            {
                Controller = new object(),
                GridPosition = entry.GridPosition,
                ControllerIdentity = "0x2001",
            }
            : entry)
        .ToArray();
    AssertEqual(
        false,
        reservation.TryMatch(pointerReplacement, out _, out _),
        "A replacement controller at the same index and position reused the old reservation.");

    var coordinateReplacement = initial
        .Select((entry, index) => index == 1
            ? new RuntimeCookerControllerEntry
            {
                Controller = entry.Controller,
                GridPosition = new RuntimeCookerGridPosition(2, 0, 0),
                ControllerIdentity = entry.ControllerIdentity,
            }
            : entry)
        .ToArray();
    AssertEqual(
        false,
        reservation.TryMatch(coordinateReplacement, out _, out _),
        "The same pointer at a different coordinate reused the old reservation.");

    AssertEqual(
        RuntimeCookerChallengeGateState.Available,
        reservation.EvaluateChallengeGate(
            new HashSet<RuntimeCookerGridPosition>(),
            couldOpen: true),
        "An exact unlocked gate was not available.");
    AssertEqual(
        RuntimeCookerChallengeGateState.Locked,
        reservation.EvaluateChallengeGate(
            new HashSet<RuntimeCookerGridPosition> { reservation.GridPosition },
            couldOpen: false),
        "A challenge lock appearing after the snapshot was not detected.");
    AssertEqual(
        RuntimeCookerChallengeGateState.Inconsistent,
        reservation.EvaluateChallengeGate(
            new HashSet<RuntimeCookerGridPosition> { reservation.GridPosition },
            couldOpen: true),
        "Contradictory LockedCookers/CouldOpen evidence was accepted.");
}

static void VerifyCookerContentSignature()
{
    var open = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    var locked = BuildSignatureState(
        couldOpen: false,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, locked),
        "Changing CouldOpen did not change the cooker content signature.");

    var challengeLocked = BuildSignatureState(
        couldOpen: false,
        automationAvailable: false,
        automationAvailability: "Unavailable",
        automationAvailabilityDiagnostic: "fixed-detail");
    challengeLocked.PlacedCookers[0] = CloneCooker(
        challengeLocked.PlacedCookers[0],
        challengeLocked: true);
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, challengeLocked),
        "Changing ChallengeLocked did not change the cooker content signature.");

    var moved = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    moved.PlacedCookers[0] = CloneCooker(
        moved.PlacedCookers[0],
        gridPosition: new CookerGridPosition { X = 9, Y = 8, Z = 7 });
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, moved),
        "Changing GridPosition did not change the cooker content signature.");

    var replaced = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    replaced.PlacedCookers[0] = CloneCooker(
        replaced.PlacedCookers[0],
        controllerIdentity: "0x9999");
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, replaced),
        "Changing ControllerIdentity did not change the cooker content signature.");

    var unavailable = BuildSignatureState(
        couldOpen: true,
        automationAvailable: false,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, unavailable),
        "Changing AutomationAvailable did not change the cooker content signature.");

    var residual = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "ExtractedResidual",
        automationAvailabilityDiagnostic: "fixed-detail");
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, residual),
        "Changing the stable automation availability diagnostic did not change the cooker content signature.");

    var changedDetail = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "changed-detail");
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, changedDetail),
        "Changing the bounded availability detail did not change the cooker content signature.");

    var incomplete = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    incomplete.PlacedCookerSnapshotComplete = false;
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, incomplete),
        "Changing snapshot completeness did not change the cooker content signature.");

    var changedCount = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    changedCount.PlacedCookerControllerCount = 3;
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, changedCount),
        "Changing the controller count did not change the cooker content signature.");

    var changedEmptyCount = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    changedEmptyCount.PlacedCookerEmptyControllerCount = 0;
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, changedEmptyCount),
        "Changing the empty-controller count did not change the cooker content signature.");

    var changedFailures = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    changedFailures.PlacedCookerReadFailureCount = 1;
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, changedFailures),
        "Changing the read failure count did not change the cooker content signature.");

    var changedLockedCount = BuildSignatureState(
        couldOpen: true,
        automationAvailable: true,
        automationAvailability: "StrictIdle",
        automationAvailabilityDiagnostic: "fixed-detail");
    changedLockedCount.PlacedCookerLockedControllerCount = 1;
    AssertNotEqual(
        RuntimeCookerSnapshotContentSignature.Append(17, open),
        RuntimeCookerSnapshotContentSignature.Append(17, changedLockedCount),
        "Changing the authoritative locked-controller count did not change the cooker content signature.");
}

static void VerifyLockedCookerSourceContracts()
{
    var root = FindRepositoryRoot();
    var reflection = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeCookerReflection.cs"));
    var snapshot = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeCookerSnapshotService.cs"));
    var highlight = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeCookerHighlightService.cs"));
    var topologyObserver = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "SpecialBusiness",
        "YuumaCookerTopologyObserver.cs"));

    var directoryRead = ExtractMethod(
        reflection,
        "public static bool TryReadCookerControllerEntriesFromCookSystem(");
    var dictionaryKey = directoryRead.IndexOf(
        "TryReadExactVector3Int(entry.Key",
        StringComparison.Ordinal);
    var lockedSetValidation = directoryRead.IndexOf(
        "allCookers=locked-grid-missing",
        StringComparison.Ordinal);
    var lockedSkip = directoryRead.IndexOf(
        "if (lockedPositions.Contains(entry.GridPosition))",
        StringComparison.Ordinal);
    var controllerGridRead = directoryRead.IndexOf(
        "TryReadControllerGridPosition(",
        StringComparison.Ordinal);
    AssertTrue(
        dictionaryKey >= 0
        && lockedSetValidation > dictionaryKey
        && lockedSkip > lockedSetValidation
        && controllerGridRead > lockedSkip,
        "AllCookers must validate exact dictionary keys, reject unknown locks, and skip locked controller GridPosition getters.");
    AssertContains(
        directoryRead[lockedSkip..controllerGridRead],
        "continue;",
        "A locked dictionary entry can fall through to controller GridPosition.");

    var snapshotRead = ExtractMethod(
        snapshot,
        "private static RuntimeCookerSnapshotReadResult ReadPlacedCookers(");
    var snapshotLockedRead = snapshotRead.IndexOf(
        "TryReadLockedCookerPositions(",
        StringComparison.Ordinal);
    var snapshotDirectoryRead = snapshotRead.IndexOf(
        "TryReadCookerControllerEntriesFromCookSystem(",
        StringComparison.Ordinal);
    var snapshotLockedSkip = snapshotRead.IndexOf(
        "if (lockedPositions.Contains(entry.GridPosition))",
        StringComparison.Ordinal);
    var snapshotStateRead = snapshotRead.IndexOf(
        "TryReadCookerControllerState(",
        StringComparison.Ordinal);
    AssertTrue(
        snapshotLockedRead >= 0
        && snapshotDirectoryRead > snapshotLockedRead
        && snapshotLockedSkip > snapshotDirectoryRead
        && snapshotStateRead > snapshotLockedSkip,
        "Snapshot reading must classify locks before reading the directory and skip locked controller state.");
    AssertContains(
        snapshotRead[snapshotLockedSkip..snapshotStateRead],
        "continue;",
        "A locked snapshot entry can fall through to state or cooker getters.");
    AssertContains(
        snapshot,
        "cookers.Count + emptyControllerCount + lockedControllerCount != controllerEntries.Count",
        "Complete snapshot classification no longer accounts for placed, empty and locked controllers exactly.");

    var beginMutation = ExtractMethod(
        highlight,
        "public static void BeginTopologyMutation(");
    AssertContains(
        beginMutation,
        "RendererBaselines[pointer] = new RendererBaseline(",
        "Topology mutation does not retain a pure managed original-color baseline.");
    AssertContains(
        beginMutation,
        "HighlightedRenderers.Clear();",
        "Topology mutation retains renderer wrappers across native destruction.");
    AssertDoesNotContain(
        beginMutation,
        "RestoreAllLocked",
        "Topology mutation writes to renderer wrappers during the native destruction prefix.");
    AssertContains(
        beginMutation,
        "try",
        "Topology mutation prefix has no no-throw boundary.");
    AssertContains(
        beginMutation,
        "catch",
        "Topology mutation prefix can escape into the native game method.");

    var completeMutation = ExtractMethod(
        highlight,
        "public static void CompleteTopologyMutation(");
    AssertContains(
        completeMutation,
        "DecrementTopologyMutationDepth()",
        "Topology mutation completion does not unwind nested mutation depth.");
    AssertContains(
        completeMutation,
        "_nextScanAt = 0f",
        "Topology mutation completion does not force a fresh open-directory scan.");
    AssertContains(
        completeMutation,
        "Interlocked.CompareExchange(ref _topologyMutationDepth, 1, 0)",
        "Topology mutation completion does not remain fail-closed after an internal failure.");
    AssertContains(
        completeMutation,
        "catch",
        "Topology mutation postfix can escape into the native game method.");

    var baselineDeclaration = ExtractDeclaration(
        highlight,
        "private readonly record struct RendererBaseline(");
    AssertContains(
        baselineDeclaration,
        "Color OriginalColor",
        "Renderer baseline does not retain the original color as a managed value.");
    AssertContains(
        baselineDeclaration,
        "bool OriginalEnabled",
        "Renderer baseline does not retain the original enabled state as a managed value.");
    AssertDoesNotContain(
        baselineDeclaration,
        "SpriteRenderer",
        "Renderer baseline retains a Unity wrapper across topology mutation.");
    AssertDoesNotContain(
        baselineDeclaration,
        "object",
        "Renderer baseline retains an opaque native wrapper across topology mutation.");

    var highlightTick = ExtractMethod(highlight, "public static void Tick(");
    AssertDoesNotContain(
        highlightTick,
        "RendererBaselines.Clear()",
        "A target change or disable can discard retained colors before fresh renderer reconciliation.");
    AssertContains(
        highlightTick,
        "if (RendererBaselines.Count == 0) return;",
        "A disabled target does not keep ticking until retained colors are reconciled.");

    var highlightScan = ExtractMethod(highlight, "private static void ScanAndApply(");
    var highlightLockedRead = highlightScan.IndexOf(
        "TryReadLockedCookerPositions(",
        StringComparison.Ordinal);
    var highlightDirectoryRead = highlightScan.IndexOf(
        "TryReadCookerControllerEntriesFromCookSystem(",
        StringComparison.Ordinal);
    var highlightLockedSkip = highlightScan.IndexOf(
        "if (lockedPositions.Contains(entry.GridPosition))",
        StringComparison.Ordinal);
    var highlightStateRead = highlightScan.IndexOf(
        "TryReadCookerControllerState(",
        StringComparison.Ordinal);
    var highlightRendererRead = highlightScan.IndexOf(
        "ReadCookerRenderers(",
        StringComparison.Ordinal);
    AssertTrue(
        highlightLockedRead >= 0
        && highlightDirectoryRead > highlightLockedRead
        && highlightLockedSkip > highlightDirectoryRead
        && highlightStateRead > highlightLockedSkip
        && highlightRendererRead > highlightStateRead,
        "Highlight scanning must classify locks before directory/state/renderer reads.");
    AssertContains(
        highlightScan[highlightLockedSkip..highlightStateRead],
        "continue;",
        "A locked highlight entry can fall through to state or renderer reads.");
    var allOpenRendererRead = highlightScan.IndexOf(
        "openRenderers.AddRange(controllerRenderers);",
        StringComparison.Ordinal);
    var targetRendererFilter = highlightScan.IndexOf(
        "if (target.Enabled && state.TypeIds.Contains(target.CookerTypeId))",
        StringComparison.Ordinal);
    var retainedRestore = highlightScan.IndexOf(
        "RestoreRetainedBaselinesLocked(openRenderers);",
        StringComparison.Ordinal);
    var disabledReturn = highlightScan.IndexOf(
        "if (!target.Enabled)",
        retainedRestore,
        StringComparison.Ordinal);
    var targetApply = highlightScan.IndexOf(
        "foreach (var renderer in targetRenderers)",
        disabledReturn,
        StringComparison.Ordinal);
    AssertTrue(
        allOpenRendererRead > highlightRendererRead
        && targetRendererFilter > allOpenRendererRead
        && retainedRestore > targetRendererFilter
        && disabledReturn > retainedRestore
        && targetApply > disabledReturn,
        "Topology recovery must scan every fresh unlocked renderer, restore A, then disable or apply target B.");

    var retainedBaselineRestore = ExtractMethod(
        highlight,
        "private static void RestoreRetainedBaselinesLocked(");
    AssertContains(
        retainedBaselineRestore,
        "freshRenderers.TryGetValue(pointer, out var renderer)",
        "Retained colors are not matched to fresh renderer wrappers by native pointer.");
    AssertContains(
        retainedBaselineRestore,
        "renderer.color = baseline.OriginalColor;",
        "A surviving old target renderer is not restored to its original color.");
    AssertContains(
        retainedBaselineRestore,
        "renderer.enabled = baseline.OriginalEnabled;",
        "A surviving old target renderer is not restored to its original enabled state.");
    AssertContains(
        retainedBaselineRestore,
        "RendererBaselines.Remove(pointer);",
        "Destroyed or locked renderer baselines are retained after the fresh open-controller scan.");

    foreach (var prefixName in new[]
             {
                 "private static void OnLockCookersPrefix(",
                 "private static void OnLockCookersForeverPrefix(",
                 "private static void OnCookerAvailabilityUpdatePrefix(",
             })
    {
        var prefix = ExtractMethod(topologyObserver, prefixName);
        var beginFrame = prefix.IndexOf("__state = BeginMutation(", StringComparison.Ordinal);
        var highlightBarrier = prefix.IndexOf(
            "if (__state != null) RuntimeCookerHighlightService.BeginTopologyMutation(",
            StringComparison.Ordinal);
        AssertTrue(
            beginFrame >= 0 && highlightBarrier > beginFrame,
            $"Topology prefix '{prefixName}' must arm the highlight barrier only for an active Yuuma mutation frame.");
    }

    foreach (var postfixName in new[]
             {
                 "private static void OnLockCookersPostfix(",
                 "private static void OnLockCookersForeverPostfix(",
                 "private static void OnCookerAvailabilityUpdatePostfix(",
             })
    {
        var postfix = ExtractMethod(topologyObserver, postfixName);
        var completeFrame = postfix.IndexOf("CompleteMutation(__state, __runOriginal);", StringComparison.Ordinal);
        var stateGate = postfix.IndexOf("if (__state != null)", StringComparison.Ordinal);
        var highlightBarrier = postfix.IndexOf(
            "RuntimeCookerHighlightService.CompleteTopologyMutation(",
            StringComparison.Ordinal);
        AssertTrue(
            completeFrame >= 0 && stateGate > completeFrame && highlightBarrier > stateGate,
            $"Topology postfix '{postfixName}' must release the highlight barrier only for the matching active Yuuma frame.");
    }
}

static RuntimeCookerControllerState BuildControllerState(
    IEnumerable<int> typeIds,
    bool couldOpen,
    bool isEmptyDesk = false,
    int phase = 0,
    object? result = null,
    object? chosenRecipe = null)
{
    return new RuntimeCookerControllerState
    {
        Cooker = new object(),
        TypeIds = typeIds.ToArray(),
        IsEmptyDesk = isEmptyDesk,
        Phase = phase,
        Result = result,
        ChosenRecipe = chosenRecipe,
        CouldOpen = couldOpen,
    };
}

static IReadOnlyList<RuntimeCookerControllerEntry> BuildControllerEntries(
    params object[] controllers)
{
    return controllers
        .Select((controller, index) => new RuntimeCookerControllerEntry
        {
            Controller = controller,
            GridPosition = new RuntimeCookerGridPosition(index, 0, 0),
            ControllerIdentity = $"0x{0x1000 + index:X}",
        })
        .ToArray();
}

static RecommendationState BuildStaleState()
{
    var state = new RecommendationState
    {
        PlacedCookerEmptyControllerCount = 1,
        PlacedCookerLockedControllerCount = 1,
        PlacedCookerStatus = "stale",
    };
    state.PlacedCookerTypeIds.Add(5);
    state.PlacedCookers.Add(new PlacedCookerInfo
    {
        ControllerIndex = 99,
        GridPosition = new CookerGridPosition { X = 99, Y = 0, Z = 0 },
        ControllerIdentity = "0xFFFF",
        TypeIds = new List<int> { 5 },
        ChallengeLocked = false,
        CouldOpen = true,
        AutomationAvailable = true,
        AutomationAvailability = "StrictIdle",
        AutomationAvailabilityDiagnostic = "startAvailability=StrictIdle; ownership=not-required",
        Source = "stale",
    });
    return state;
}

static RecommendationState BuildSignatureState(
    bool couldOpen,
    bool automationAvailable,
    string automationAvailability,
    string automationAvailabilityDiagnostic)
{
    var state = new RecommendationState
    {
        PlacedCookerSnapshotComplete = true,
        PlacedCookerControllerCount = 2,
        PlacedCookerEmptyControllerCount = 1,
        PlacedCookerLockedControllerCount = 0,
        PlacedCookerReadFailureCount = 0,
        PlacedCookerStatus = "fixed-status",
    };
    state.PlacedCookerTypeIds.Add(3);
    state.PlacedCookers.Add(new PlacedCookerInfo
    {
        ControllerIndex = 1,
        GridPosition = new CookerGridPosition { X = 1, Y = 0, Z = 0 },
        ControllerIdentity = "0x1001",
        TypeIds = new List<int> { 3 },
        ChallengeLocked = false,
        CouldOpen = couldOpen,
        AutomationAvailable = automationAvailable,
        AutomationAvailability = automationAvailability,
        AutomationAvailabilityDiagnostic = automationAvailabilityDiagnostic,
        Source = "CookSystemManager",
    });
    return state;
}

static PlacedCookerInfo CloneCooker(
    PlacedCookerInfo source,
    CookerGridPosition? gridPosition = null,
    string? controllerIdentity = null,
    bool? challengeLocked = null)
{
    return new PlacedCookerInfo
    {
        ControllerIndex = source.ControllerIndex,
        GridPosition = gridPosition ?? source.GridPosition,
        ControllerIdentity = controllerIdentity ?? source.ControllerIdentity,
        TypeIds = source.TypeIds.ToList(),
        TypeNames = source.TypeNames.ToList(),
        Name = source.Name,
        ChallengeLocked = challengeLocked ?? source.ChallengeLocked,
        CouldOpen = source.CouldOpen,
        AutomationAvailable = source.AutomationAvailable,
        AutomationAvailability = source.AutomationAvailability,
        AutomationAvailabilityDiagnostic = source.AutomationAvailabilityDiagnostic,
        Source = source.Source,
    };
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

    throw new DirectoryNotFoundException("Could not locate the repository root from the smoke output directory.");
}

static string ExtractMethod(string source, string signature)
{
    var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
    if (signatureIndex < 0)
    {
        throw new InvalidOperationException($"Source method not found: {signature}");
    }

    var openingBrace = source.IndexOf('{', signatureIndex);
    if (openingBrace < 0)
    {
        throw new InvalidOperationException($"Source method has no body: {signature}");
    }

    var depth = 0;
    var inString = false;
    var inCharacter = false;
    var escaped = false;
    for (var index = openingBrace; index < source.Length; index++)
    {
        var current = source[index];
        if (escaped)
        {
            escaped = false;
            continue;
        }

        if ((inString || inCharacter) && current == '\\')
        {
            escaped = true;
            continue;
        }

        if (!inCharacter && current == '"')
        {
            inString = !inString;
            continue;
        }

        if (!inString && current == '\'')
        {
            inCharacter = !inCharacter;
            continue;
        }

        if (inString || inCharacter) continue;
        if (current == '{')
        {
            depth++;
        }
        else if (current == '}' && --depth == 0)
        {
            return source[signatureIndex..(index + 1)];
        }
    }

    throw new InvalidOperationException($"Source method body is incomplete: {signature}");
}

static string ExtractDeclaration(string source, string signature)
{
    var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
    if (signatureIndex < 0)
    {
        throw new InvalidOperationException($"Source declaration not found: {signature}");
    }

    var terminator = source.IndexOf(");", signatureIndex, StringComparison.Ordinal);
    if (terminator < 0)
    {
        throw new InvalidOperationException($"Source declaration is incomplete: {signature}");
    }

    return source[signatureIndex..(terminator + 2)];
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertNotEqual<T>(T left, T right, string message)
{
    if (EqualityComparer<T>.Default.Equals(left, right))
    {
        throw new InvalidOperationException($"{message} Both values were '{left}'.");
    }
}

static void AssertContains(string actual, string expected, string message)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Expected to find '{expected}' in '{actual}'.");
    }
}

static void AssertDoesNotContain(string actual, string expected, string message)
{
    if (actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Did not expect to find '{expected}' in '{actual}'.");
    }
}
