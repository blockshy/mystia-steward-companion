using BepInEx.Logging;
using MystiaStewardCompanion.Save;

try
{
    var mainThreadId = Environment.CurrentManagedThreadId;
    var log = new ManualLogSource();
    RuntimeNightBusinessAutomationGate.Initialize(mainThreadId, log);

    var inactive = RuntimeNightBusinessAutomationGate.Refresh();
    AssertFalse(inactive.Allowed, "Inactive business unexpectedly allowed automation.");
    AssertEqual(RuntimeNightBusinessAutomationGate.LifecycleUnavailableReason, inactive.BlockReason,
        "Inactive business used the wrong block reason.");

    SetLifecycle(7, 1, NightBusinessLifecyclePhase.Active);
    RuntimeReflectionUtility.DirectorTypeAvailable = false;
    var unavailable = RuntimeNightBusinessAutomationGate.Refresh();
    AssertFalse(unavailable.Allowed, "Missing exact runtime type unexpectedly allowed automation.");
    AssertEqual(RuntimeNightBusinessAutomationGate.TutorialStateUnavailableReason, unavailable.BlockReason,
        "Missing exact runtime type used the wrong fail-closed reason.");

    RuntimeReflectionUtility.DirectorTypeAvailable = true;
    var director = new NightScene.NightSceneDirector { IsInTutorial = false };
    DEYU.Singletons.MonoSingleton<NightScene.NightSceneDirector>.Instance = director;
    var allowed = RuntimeNightBusinessAutomationGate.Refresh();
    AssertTrue(allowed.Allowed, "A readable non-tutorial business did not allow automation.");
    AssertEqual("", allowed.BlockReason, "Allowed automation retained a block reason.");

    var publishedBeforeOffThreadRead = RuntimeNightBusinessAutomationGate.Snapshot;
    var offThread = Task.Run(RuntimeNightBusinessAutomationGate.Refresh).GetAwaiter().GetResult();
    AssertFalse(offThread.Allowed, "A background-thread read unexpectedly allowed automation.");
    AssertEqual(RuntimeNightBusinessAutomationGate.TutorialStateUnavailableReason, offThread.BlockReason,
        "A background-thread read used the wrong fail-closed reason.");
    AssertEqual(publishedBeforeOffThreadRead, RuntimeNightBusinessAutomationGate.Snapshot,
        "A rejected background-thread read poisoned the published main-thread snapshot.");

    director.IsInTutorial = true;
    var tutorial = RuntimeNightBusinessAutomationGate.Refresh();
    AssertFalse(tutorial.Allowed, "Tutorial business unexpectedly allowed automation.");
    AssertTrue(tutorial.TutorialLatched, "Tutorial business was not latched to its generation.");
    AssertEqual(RuntimeNightBusinessAutomationGate.TutorialActiveReason, tutorial.BlockReason,
        "Tutorial business used the wrong block reason.");

    director.IsInTutorial = false;
    var tutorialGap = RuntimeNightBusinessAutomationGate.Refresh();
    AssertFalse(tutorialGap.Allowed, "A false gap inside the tutorial generation reopened automation.");
    AssertTrue(tutorialGap.TutorialLatched, "The tutorial generation latch was lost after a false gap.");

    SetLifecycle(7, 2, NightBusinessLifecyclePhase.Closing);
    var closing = RuntimeNightBusinessAutomationGate.Refresh();
    AssertFalse(closing.Allowed, "Closing business unexpectedly allowed automation.");
    AssertFalse(closing.TutorialLatched, "Closing did not clear the tutorial generation latch.");

    SetLifecycle(8, 3, NightBusinessLifecyclePhase.Active);
    var nextBusiness = RuntimeNightBusinessAutomationGate.Refresh();
    AssertTrue(nextBusiness.Allowed, "The previous tutorial latch leaked into the next business generation.");

    DEYU.Singletons.MonoSingleton<NightScene.NightSceneDirector>.Instance = null;
    var missingInstance = RuntimeNightBusinessAutomationGate.Refresh();
    AssertFalse(missingInstance.Allowed, "A missing exact singleton unexpectedly allowed automation.");
    AssertEqual(RuntimeNightBusinessAutomationGate.TutorialStateUnavailableReason, missingInstance.BlockReason,
        "A missing exact singleton used the wrong fail-closed reason.");

    DEYU.Singletons.MonoSingleton<NightScene.NightSceneDirector>.Instance = director;
    director.ThrowOnTutorialRead = true;
    var throwingGetter = RuntimeNightBusinessAutomationGate.Refresh();
    AssertFalse(throwingGetter.Allowed, "A throwing IsInTutorial getter unexpectedly allowed automation.");
    AssertEqual(RuntimeNightBusinessAutomationGate.TutorialStateUnavailableReason, throwingGetter.BlockReason,
        "A throwing IsInTutorial getter used the wrong fail-closed reason.");
    AssertTrue(throwingGetter.Status.Contains("simulated native tutorial read failure", StringComparison.Ordinal),
        "A throwing IsInTutorial getter did not preserve a useful bounded diagnostic.");
    director.ThrowOnTutorialRead = false;
    AssertTrue(RuntimeNightBusinessAutomationGate.Refresh().Allowed,
        "The gate did not recover after a transient IsInTutorial getter failure.");

    AssertSourceContract();
    AssertIntegrationContract();
    Console.WriteLine("PASS: tutorial automation gate uses the exact main-thread runtime state, fails closed, and latches per business generation.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void SetLifecycle(long generation, long version, NightBusinessLifecyclePhase phase)
{
    RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessLifecycleSnapshot(
        generation,
        version,
        phase,
        "test",
        DateTime.UtcNow,
        Environment.CurrentManagedThreadId);
}

static void AssertSourceContract()
{
    var assembly = typeof(RuntimeNightBusinessAutomationGate).Assembly;
    using var stream = assembly.GetManifestResourceStream("RuntimeNightBusinessAutomationGate.cs")
        ?? throw new InvalidOperationException("Automation gate source resource was not embedded.");
    using var reader = new StreamReader(stream);
    var source = reader.ReadToEnd();

    foreach (var required in new[]
             {
                 "NightScene.NightSceneDirector",
                 "DEYU.Singletons.MonoSingleton`1",
                 "get_Instance",
                 "IsInTutorial",
                 "BindingFlags.DeclaredOnly",
                 "currentThreadId != _mainThreadId",
                 "_tutorialGeneration == lifecycle.Generation",
             })
    {
        AssertTrue(source.Contains(required, StringComparison.Ordinal),
            $"Exact tutorial automation contract was missing: {required}.");
    }

    foreach (var prohibited in new[]
             {
                 "GetSingletonInstance(",
                 "FindUnityObject(",
                 "StartTutorial(",
                 "NS_RunTUT",
             })
    {
        AssertFalse(source.Contains(prohibited, StringComparison.Ordinal),
            $"Tutorial automation gate restored a heuristic or broad runtime path: {prohibited}.");
    }
}

static void AssertIntegrationContract()
{
    var assembly = typeof(RuntimeNightBusinessAutomationGate).Assembly;
    var preparation = ReadResource(assembly, "RuntimeOrderPreparationService.cs");
    var controller = ReadResource(assembly, "StewardOverlayController.cs");
    var models = ReadResource(assembly, "LocalApiModels.cs");

    AssertEqual(3, CountOccurrences(preparation, "if (!automationGate.Allowed) return BuildAutomationGateUnavailableResult("),
        "The three automation action entries did not share the tutorial gate.");
    AssertFalse(preparation.Contains("EnsureLifecycleSessionActive(", StringComparison.Ordinal),
        "An old lifecycle-only stage checkpoint remained.");
    AssertTrue(CountOccurrences(preparation, "EnsureAutomationSessionActive(") >= 11,
        "Automation stages were not all routed through the tutorial-aware checkpoint.");
    AssertTrue(preparation.Contains("HandleBlockedAutomationCookingJobs(automationGate)", StringComparison.Ordinal),
        "Cooking-job polling did not enforce the tutorial gate.");
    AssertTrue(preparation.Contains(
            "ClearAutomationCookingJobs(\n            RuntimeNightBusinessAutomationGate.TutorialActiveReason)",
            StringComparison.Ordinal),
        "Confirmed tutorial state did not release Mod cooking-job ownership.");
    var blockedJobsStart = preparation.IndexOf(
        "private static AutomationCookingProcessResult HandleBlockedAutomationCookingJobs(",
        StringComparison.Ordinal);
    var blockedJobsEnd = preparation.IndexOf("private static int ToProgressBucket", blockedJobsStart, StringComparison.Ordinal);
    var blockedJobs = preparation[blockedJobsStart..blockedJobsEnd];
    var nonTutorialReturn = blockedJobs.IndexOf(
        "return new AutomationCookingProcessResult(Array.Empty<string>(), false);",
        StringComparison.Ordinal);
    var suspendJobs = blockedJobs.IndexOf("SuspendAutomationCookingJobClocks();", StringComparison.Ordinal);
    var clearJobs = blockedJobs.IndexOf("ClearAutomationCookingJobs(", StringComparison.Ordinal);
    AssertTrue(suspendJobs >= 0 && nonTutorialReturn > suspendJobs && clearJobs > nonTutorialReturn,
        "An unavailable tutorial-state read no longer paused jobs without clearing ownership.");

    var updateStart = controller.IndexOf("public void Update()", StringComparison.Ordinal);
    var refresh = controller.IndexOf("ProcessNightBusinessAutomationGateChange();", updateStart, StringComparison.Ordinal);
    var pending = controller.IndexOf("ProcessPendingOrderPreparations();", updateStart, StringComparison.Ordinal);
    AssertTrue(updateStart >= 0 && refresh > updateStart && pending > refresh,
        "The Unity Update loop did not refresh the tutorial gate before pending automation commands.");
    foreach (var field in new[]
             {
                 "NightBusinessAutomationAllowed",
                 "NightBusinessAutomationBlockReason",
                 "RuntimeNightBusinessAutomationStatus",
             })
    {
        AssertTrue(models.Contains(field, StringComparison.Ordinal),
            $"Local API snapshot model was missing {field}.");
        AssertTrue(controller.Contains($"snapshot.{field}", StringComparison.Ordinal),
            $"Local API snapshot signature omitted {field}.");
    }
}

static string ReadResource(System.Reflection.Assembly assembly, string name)
{
    using var stream = assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Source resource was not embedded: {name}.");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static int CountOccurrences(string source, string value)
{
    var count = 0;
    var offset = 0;
    while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
    {
        count++;
        offset += value.Length;
    }
    return count;
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
