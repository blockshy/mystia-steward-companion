using System.Reflection;
using BepInEx.Logging;
using GameData.RunTime.Common;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.Save;

try
{
    AggregateModLogService.Reset();
    RuntimeStorageProbe.Reset();
    var log = new ManualLogSource();
    RuntimeStorageSentinelDiagnostic.Attach(log);
    AssertEqual("patched=13", RuntimeStorageSentinelDiagnostic.Status,
        "The exact storage diagnostic hook set was not installed.");
    AssertTrue(
        log.Messages.Any(message => message.Contains("12 typed", StringComparison.Ordinal)),
        "Successful hook installation was not reported.");
    AssertExactPatchSet();
    AssertSourceContract();

    RunTimeStorage.FoodOut(12, false);
    AssertEqual(1, RuntimeStorageProbe.Calls.Count, "A normal original call did not run.");
    AssertEqual(0, AggregateModLogService.Entries.Count,
        "A non-sentinel object ID produced diagnostics.");

    RunTimeStorage.FoodOut(-1, false);
    AssertEqual(2, RuntimeStorageProbe.Calls.Count,
        "The original call was skipped while aggregate logging was disabled.");
    AssertEqual(0, AggregateModLogService.Entries.Count,
        "A disabled aggregate log still received diagnostics.");

    AggregateModLogService.Enabled = true;
    RuntimeNightBusinessLifecycle.Snapshot =
        new NightBusinessLifecycleSnapshot(NightBusinessLifecyclePhase.Inactive, 0);
    RunTimeStorage.FoodOut(-1, false);
    AssertEqual(3, RuntimeStorageProbe.Calls.Count,
        "The original call was skipped outside an active business.");
    AssertEqual(0, AggregateModLogService.Entries.Count,
        "An inactive business produced sentinel diagnostics.");

    RuntimeNightBusinessLifecycle.Snapshot =
        new NightBusinessLifecycleSnapshot(NightBusinessLifecyclePhase.Active, 1);
    InvokeEveryWrapper(suppressCallbacks: false);
    AssertEqual(12, AggregateModLogService.Entries.Count,
        "The six single and six range categories were not all observed.");
    foreach (var entryName in GetWrapperNames())
    {
        AssertTrue(
            AggregateModLogService.Entries.Any(entry =>
                entry.Content.Contains($"entry={entryName};", StringComparison.Ordinal)),
            $"The sentinel log did not identify {entryName}.");
    }
    AssertTrue(
        AggregateModLogService.Entries.All(entry =>
            entry.Channel == "runtime-storage"
            && entry.Content.Contains("generation=1;", StringComparison.Ordinal)
            && entry.Content.Contains("id=-1;", StringComparison.Ordinal)),
        "Sentinel diagnostics omitted their bounded runtime identity.");

    var beforeRepeatedCalls = RuntimeStorageProbe.Calls.Count;
    for (var index = 0; index < 12; index++)
    {
        RunTimeStorage.FoodOut(-1, false);
    }
    var foodEntries = AggregateModLogService.Entries.Count(entry =>
        entry.Content.Contains("entry=FoodOut;", StringComparison.Ordinal));
    AssertEqual(5, foodEntries,
        "Per-key diagnostics did not retain four details and one suppression notice.");
    AssertEqual(beforeRepeatedCalls + 12, RuntimeStorageProbe.Calls.Count,
        "Duplicate suppression changed original storage execution.");

    var generationOneCount = AggregateModLogService.Entries.Count;
    RuntimeNightBusinessLifecycle.Snapshot =
        new NightBusinessLifecycleSnapshot(NightBusinessLifecyclePhase.Active, 2);
    InvokeEveryWrapper(suppressCallbacks: false);
    InvokeEveryWrapper(suppressCallbacks: true);
    RunTimeStorage.FoodOut(-1, false);
    var generationTwoEntries = AggregateModLogService.Entries.Skip(generationOneCount).ToList();
    AssertEqual(25, generationTwoEntries.Count,
        "Generation-wide diagnostics did not retain 24 events and one limit notice.");
    AssertEqual(
        1,
        generationTwoEntries.Count(entry =>
            entry.Content.Contains("storage-out-negative-id-suppressed", StringComparison.Ordinal)),
        "Generation-wide suppression was not reported exactly once.");

    var beforeGenerationReset = AggregateModLogService.Entries.Count;
    RuntimeNightBusinessLifecycle.Snapshot =
        new NightBusinessLifecycleSnapshot(NightBusinessLifecyclePhase.Active, 3);
    RunTimeStorage.FoodOut(-1, false);
    var resetEntry = AggregateModLogService.Entries[beforeGenerationReset];
    AssertTrue(
        resetEntry.Content.Contains("generation=3;", StringComparison.Ordinal)
        && resetEntry.Content.Contains("occurrence=1;", StringComparison.Ordinal),
        "A new business generation did not reset diagnostic bounds.");

    RuntimeNightBusinessLifecycle.Snapshot =
        new NightBusinessLifecycleSnapshot(NightBusinessLifecyclePhase.Active, 4);
    AggregateModLogService.ThrowOnAppend = true;
    var beforeSinkFailure = RuntimeStorageProbe.Calls.Count;
    RunTimeStorage.ItemOut(-1, false);
    AssertEqual(beforeSinkFailure + 1, RuntimeStorageProbe.Calls.Count,
        "A diagnostic sink failure escaped into the original storage call.");
    AggregateModLogService.ThrowOnAppend = false;

    RuntimeNightBusinessLifecycle.Snapshot =
        new NightBusinessLifecycleSnapshot(NightBusinessLifecyclePhase.Active, 5);
    RuntimeStorageProbe.ThrowOnNextObjectOut = true;
    AssertThrows<InvalidOperationException>(
        () => RunTimeStorage.CookerOut(-1, true),
        "The original storage exception was swallowed or replaced.");
    RunTimeStorage.BeverageOut(-1, true);
    var lastEntry = AggregateModLogService.Entries[^1];
    AssertTrue(
        lastEntry.Content.Contains("entry=BeverageOut;", StringComparison.Ordinal)
        && !lastEntry.Content.Contains("entry=CookerOut;", StringComparison.Ordinal),
        "The wrapper finalizer did not clear an exceptional caller context.");

    Console.WriteLine(
        "PASS: runtime storage sentinel diagnostics preserve native calls and exceptions, "
        + "identify all exact wrappers, and bound aggregate output.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static string[] GetWrapperNames()
{
    return new[]
    {
        "BadgeOut",
        "BadgeOutRange",
        "BeverageOut",
        "BeverageOutRange",
        "CookerOut",
        "CookerOutRange",
        "FoodOut",
        "FoodOutRange",
        "IngredientOut",
        "IngredientOutRange",
        "ItemOut",
        "ItemOutRange",
    };
}

static void InvokeEveryWrapper(bool suppressCallbacks)
{
    RunTimeStorage.BadgeOut(-1, suppressCallbacks);
    RunTimeStorage.BadgeOutRange(new TestEnumerable<int>(), suppressCallbacks);
    RunTimeStorage.BeverageOut(-1, suppressCallbacks);
    RunTimeStorage.BeverageOutRange(new TestEnumerable<int>(), suppressCallbacks);
    RunTimeStorage.CookerOut(-1, suppressCallbacks);
    RunTimeStorage.CookerOutRange(new Il2CppStructArray<int>(), suppressCallbacks);
    RunTimeStorage.FoodOut(-1, suppressCallbacks);
    RunTimeStorage.FoodOutRange(new TestEnumerable<int>(), suppressCallbacks);
    RunTimeStorage.IngredientOut(-1, suppressCallbacks);
    RunTimeStorage.IngredientOutRange(new TestEnumerable<int>(), suppressCallbacks);
    RunTimeStorage.ItemOut(-1, suppressCallbacks);
    RunTimeStorage.ItemOutRange(new TestEnumerable<int>(), suppressCallbacks);
}

static void AssertExactPatchSet()
{
    var storageType = typeof(RunTimeStorage);
    foreach (var wrapperName in GetWrapperNames())
    {
        var method = storageType.GetMethod(
            wrapperName,
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing test wrapper {wrapperName}.");
        var patchInfo = Harmony.GetPatchInfo(method)
            ?? throw new InvalidOperationException($"{wrapperName} was not patched.");
        AssertTrue(
            patchInfo.Prefixes.Any(patch =>
                patch.owner == "com.tyukki.mystia-steward-companion.runtime-storage-sentinel-diagnostic"),
            $"{wrapperName} did not receive the exact prefix.");
        AssertTrue(
            patchInfo.Finalizers.Any(patch =>
                patch.owner == "com.tyukki.mystia-steward-companion.runtime-storage-sentinel-diagnostic"),
            $"{wrapperName} did not receive the exact finalizer.");
    }

    var objectOut = storageType.GetMethod(
        "ObjectOut",
        BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing test ObjectOut.");
    var objectOutPatches = Harmony.GetPatchInfo(objectOut)
        ?? throw new InvalidOperationException("ObjectOut was not patched.");
    AssertTrue(
        objectOutPatches.Prefixes.Any(patch =>
            patch.owner == "com.tyukki.mystia-steward-companion.runtime-storage-sentinel-diagnostic"),
        "ObjectOut did not receive the observation prefix.");
    AssertEqual(0, objectOutPatches.Postfixes.Count,
        "ObjectOut received a behavior-changing postfix.");
    AssertEqual(0, objectOutPatches.Finalizers.Count,
        "ObjectOut received a behavior-changing finalizer.");
}

static void AssertSourceContract()
{
    var assembly = typeof(RuntimeStorageSentinelDiagnostic).Assembly;
    using var stream = assembly.GetManifestResourceStream("RuntimeStorageSentinelDiagnostic.cs")
        ?? throw new InvalidOperationException("Sentinel diagnostic source was not embedded.");
    using var reader = new StreamReader(stream);
    var source = reader.ReadToEnd();

    foreach (var forbiddenSource in new[]
             {
                 "ObjectOutRange",
                 "RecipeOut",
                 "RemoveFromStorage",
                 "GetEnumerator",
                 "GetMemberValue",
                 "objectPool.",
                 "Environment.StackTrace",
             })
    {
        AssertTrue(
            !source.Contains(forbiddenSource, StringComparison.Ordinal),
            $"Sentinel diagnostics introduced forbidden behavior: {forbiddenSource}.");
    }
    AssertTrue(
        source.Contains("if (objectId != ObservedSentinelId || !AggregateModLogService.Enabled) return;",
            StringComparison.Ordinal),
        "The sentinel and aggregate-log guard changed.");
    AssertTrue(
        source.Contains("if (!lifecycle.IsActive || lifecycle.Generation <= 0) return;",
            StringComparison.Ordinal),
        "The active business generation guard changed.");
    AssertTrue(
        source.Contains("if (!method.IsPublic", StringComparison.Ordinal),
        "The BepInEx 783 public ObjectOut wrapper visibility gate changed.");
    AssertTrue(
        source.Contains("return __exception;", StringComparison.Ordinal),
        "Wrapper finalizers no longer preserve native exceptions.");
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
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"{message} Expected {typeof(TException).Name}, actual {ex.GetType().Name}.",
            ex);
    }

    throw new InvalidOperationException(
        $"{message} Expected {typeof(TException).Name}, no exception was thrown.");
}

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected '{expected}', actual '{actual}'.");
    }
}
