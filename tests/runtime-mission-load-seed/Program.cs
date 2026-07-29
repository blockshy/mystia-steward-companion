using System.Text;
using System.Text.Json;
using MystiaStewardCompanion.Save;

try
{
    AssertCoreAndSparseDlcSelection();
    AssertDateShiftAndBucketCollision();
    AssertInvalidShapesFailClosed();
    AssertTrackingUniquenessAndFinishedMultiplicity();
    AssertBounds();
    AssertCheckedDateArithmetic();
    AssertOptionalRealSaveFixture();

    Console.WriteLine(
        "PASS: mission load seeds are parsed with strict bounded shapes, "
        + "selected by exact DLC identity, and merged with native date shifts.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertCoreAndSparseDlcSelection()
{
    var json = SaveJson(
        coreBuckets: new Dictionary<string, object[]>
        {
            ["-1"] = new[]
            {
                Task("core-permanent", new[] { false }, new[] { Array.Empty<int>() }),
            },
            ["7"] = new[]
            {
                Task(
                    "core-timed",
                    new[] { true, false },
                    new[] { Array.Empty<int>(), new[] { 1, 2 } }),
            },
        },
        coreFinished: new[] { "core-finished" },
        dlc: new Dictionary<string, object>
        {
            ["DLC1"] = Dlc(
                saveDate: 10,
                buckets: new Dictionary<string, object[]>
                {
                    ["0"] = new[]
                    {
                        Task("dlc1-task", new[] { true }, new[] { new[] { 3 } }),
                    },
                },
                finished: new[] { "dlc1-finished" }),
            ["DLC3"] = Dlc(
                saveDate: 20,
                buckets: new Dictionary<string, object[]>
                {
                    ["1"] = new[]
                    {
                        Task("dlc3-excluded", new[] { false }, new[] { Array.Empty<int>() }),
                    },
                },
                finished: new[] { "dlc3-finished" }),
        });

    var seed = RuntimeMissionLoadSeedParser.Parse(json);
    AssertEqual("BetaV102", seed.FileVersion, "File version was not parsed.");
    AssertEqual(20, seed.SavedGameDay, "Saved game date was not parsed.");
    AssertEqual(4, seed.TrackingMissionCount, "Tracking mission total is incorrect.");
    AssertEqual(3, seed.FinishedMissionCount, "Finished mission total is incorrect.");
    AssertEqual(2, seed.DlcPartitions.Count, "Sparse DLC partitions were not preserved.");

    var coreTask = seed.Core.Buckets.Single(bucket => bucket.SourceBucket == 7).Tasks.Single();
    AssertTrue(coreTask.SourceIsCore, "Core task lost its source partition kind.");
    AssertEqual("CORE", coreTask.SourcePartition, "Core task lost its source partition.");
    AssertEqual(7, coreTask.SourceBucket, "Core task lost its source bucket.");
    AssertEqual(0, coreTask.SourceOrdinal, "Core task lost its source ordinal.");
    AssertEqual(2, coreTask.FinishStateCount, "Finish-state count was not captured.");
    AssertEqual(1, coreTask.TrueFinishStateCount, "True finish-state count was not captured.");
    AssertEqual(2, coreTask.ConditionDataCount, "Condition-data count was not captured.");

    var selection = RuntimeMissionLoadSeedParser.SelectAndMerge(
        seed,
        currentDate: 20,
        new HashSet<string>(new[] { "DLC1", "DLC5" }, StringComparer.Ordinal));
    AssertSequenceEqual(
        new[] { "DLC1" },
        selection.SelectedDlcPartitions,
        "A missing sparse DLC partition was treated as an error or an excluded DLC was included.");
    AssertSequenceEqual(
        new[] { "core-permanent", "core-timed", "dlc1-task" },
        selection.Tasks.Select(task => task.Source.Label),
        "Selected task order or DLC exclusion is incorrect.");
    AssertSequenceEqual(
        new[] { "core-finished", "dlc1-finished" },
        selection.FinishedMissionLabels,
        "Selected finished labels are incorrect.");
    AssertFalse(
        selection.Tasks.Any(task => task.Source.Label == "dlc3-excluded"),
        "An unselected DLC task leaked into the merged seed.");
    AssertEqual(
        10,
        selection.Tasks.Single(task => task.Source.Label == "dlc1-task").MergedBucket,
        "DLC date shift is incorrect.");
}

static void AssertDateShiftAndBucketCollision()
{
    var seed = RuntimeMissionLoadSeedParser.Parse(SaveJson(
        coreBuckets: new Dictionary<string, object[]>
        {
            ["-1"] = new[]
            {
                Task("core-permanent", Array.Empty<bool>(), Array.Empty<int[]>()),
            },
            ["10"] = new[]
            {
                Task("core-collision", new[] { false }, new[] { Array.Empty<int>() }),
            },
        },
        dlc: new Dictionary<string, object>
        {
            ["DLC1"] = Dlc(
                saveDate: 10,
                buckets: new Dictionary<string, object[]>
                {
                    ["-1"] = new[]
                    {
                        Task("dlc-permanent", new[] { false }, new[] { Array.Empty<int>() }),
                    },
                    ["0"] = new[]
                    {
                        Task("dlc-collision", new[] { false }, new[] { Array.Empty<int>() }),
                    },
                }),
            ["DLC2"] = Dlc(
                saveDate: -1,
                buckets: new Dictionary<string, object[]>
                {
                    ["2"] = new[]
                    {
                        Task("dlc-no-date", new[] { false }, new[] { Array.Empty<int>() }),
                    },
                }),
        }));

    var selection = RuntimeMissionLoadSeedParser.SelectAndMerge(
        seed,
        currentDate: 20,
        new HashSet<string>(new[] { "DLC1", "DLC2" }, StringComparer.Ordinal));
    var permanent = selection.Buckets.Single(bucket => bucket.Bucket == -1);
    AssertSequenceEqual(
        new[] { "core-permanent", "dlc-permanent" },
        permanent.Tasks.Select(task => task.Source.Label),
        "Permanent buckets did not merge in source order.");
    var collision = selection.Buckets.Single(bucket => bucket.Bucket == 10);
    AssertSequenceEqual(
        new[] { "core-collision", "dlc-collision" },
        collision.Tasks.Select(task => task.Source.Label),
        "Shifted bucket collision did not merge in source order.");
    AssertEqual(
        22,
        selection.Tasks.Single(task => task.Source.Label == "dlc-no-date").MergedBucket,
        "dlcSaveDate=-1 did not use zero as the native date origin.");
}

static void AssertInvalidShapesFailClosed()
{
    AssertFormatException(
        "{\"playerPartial\":{\"gameDate\":{\"day\":1}},\"schedulerPartial\":{},\"schedulerPartialDLC\":{}}",
        "Missing fileVersion was accepted.");
    AssertFormatException(
        "{\"fileVersion\":\"v\",\"fileVersion\":\"v2\",\"playerPartial\":{\"gameDate\":{\"day\":1}},"
        + "\"schedulerPartial\":{\"allTrackingMissions\":{},\"finishedMissions\":[]},"
        + "\"schedulerPartialDLC\":{}}",
        "Duplicate required property was accepted.");
    AssertFormatException(
        SaveJson(day: "1"),
        "A string game date was accepted.");
    AssertFormatException(
        SaveJson(coreBuckets: new Dictionary<string, object[]> { ["-2"] = Array.Empty<object>() }),
        "A bucket below -1 was accepted.");
    AssertFormatException(
        SaveJson(coreBuckets: new Dictionary<string, object[]>
        {
            ["0"] = new[] { Task("bad-flags", new object[] { 1 }, Array.Empty<int[]>()) },
        }),
        "A non-boolean finish state was accepted.");
    AssertFormatException(
        SaveJson(coreBuckets: new Dictionary<string, object[]>
        {
            ["0"] = new[]
            {
                new
                {
                    missionLabel = "bad-data",
                    conditionFinishStates = Array.Empty<bool>(),
                    conditionData = new object[] { "not-an-array" },
                },
            },
        }),
        "A non-array condition-data entry was accepted.");
    AssertFormatException(
        SaveJson(dlc: new Dictionary<string, object>
        {
            ["DLC1"] = Dlc(-2),
        }),
        "A DLC save date below -1 was accepted.");
}

static void AssertTrackingUniquenessAndFinishedMultiplicity()
{
    AssertFormatException(
        SaveJson(
            coreBuckets: new Dictionary<string, object[]>
            {
                ["0"] = new[]
                {
                    Task("duplicate", new[] { false }, new[] { Array.Empty<int>() }),
                },
            },
            dlc: new Dictionary<string, object>
            {
                ["DLC1"] = Dlc(
                    1,
                    new Dictionary<string, object[]>
                    {
                        ["0"] = new[]
                        {
                            Task("duplicate", new[] { true }, new[] { Array.Empty<int>() }),
                        },
                    }),
            }),
        "A globally duplicate tracking label was accepted.");

    var repeatedFinished = RuntimeMissionLoadSeedParser.Parse(SaveJson(
        coreFinished: new[]
        {
            "finished-duplicate",
            "finished-duplicate",
            "core-only",
        },
        dlc: new Dictionary<string, object>
        {
            ["DLC1"] = Dlc(
                1,
                finished: new[]
                {
                    "finished-duplicate",
                    "dlc-only",
                }),
        }));
    AssertEqual(
        5,
        repeatedFinished.FinishedMissionCount,
        "Repeated finished labels were collapsed during bounded parsing.");
    var selection = RuntimeMissionLoadSeedParser.SelectAndMerge(
        repeatedFinished,
        currentDate: 20,
        new HashSet<string>(new[] { "DLC1" }, StringComparer.Ordinal));
    AssertSequenceEqual(
        new[]
        {
            "finished-duplicate",
            "finished-duplicate",
            "core-only",
            "finished-duplicate",
            "dlc-only",
        },
        selection.FinishedMissionLabels,
        "Finished labels lost their source order or multiplicity during selection.");
}

static void AssertBounds()
{
    AssertThrows<FormatException>(
        () => RuntimeMissionLoadSeedParser.Parse(
            new string('x', RuntimeMissionLoadSeedParser.MaxJsonBytes + 1)),
        "An oversized JSON payload was accepted.");
    AssertFormatException(
        SaveJson(fileVersion: new string('v', RuntimeMissionLoadSeedParser.MaxFileVersionLength + 1)),
        "An oversized file version was accepted.");
    AssertFormatException(
        SaveJson(coreBuckets: new Dictionary<string, object[]>
        {
            ["0"] = new[]
            {
                Task(
                    "too-many-flags",
                    Enumerable.Repeat(false, RuntimeMissionLoadSeedParser.MaxConditionEntriesPerTask + 1).ToArray(),
                    Array.Empty<int[]>()),
            },
        }),
        "A task with too many finish states was accepted.");
    AssertFormatException(
        SaveJson(coreBuckets: new Dictionary<string, object[]>
        {
            ["0"] = new[]
            {
                Task(
                    "too-many-condition-entries",
                    Array.Empty<bool>(),
                    Enumerable.Range(
                            0,
                            RuntimeMissionLoadSeedParser.MaxConditionEntriesPerTask + 1)
                        .Select(_ => Array.Empty<int>())
                        .ToArray()),
            },
        }),
        "A task with too many condition-data entries was accepted.");

    var tooManyBuckets = new Dictionary<string, object[]>();
    for (var index = 0; index <= RuntimeMissionLoadSeedParser.MaxBucketsPerPartition; index++)
    {
        tooManyBuckets[index.ToString()] = Array.Empty<object>();
    }
    AssertFormatException(
        SaveJson(coreBuckets: tooManyBuckets),
        "A partition with too many buckets was accepted.");

    var tooManyTasks = Enumerable.Range(
            0,
            RuntimeMissionLoadSeedParser.MaxTrackingMissions + 1)
        .Select(index => Task(
            $"task-{index}",
            Array.Empty<bool>(),
            Array.Empty<int[]>()))
        .ToArray();
    AssertFormatException(
        SaveJson(coreBuckets: new Dictionary<string, object[]> { ["0"] = tooManyTasks }),
        "Too many tracking missions were accepted.");

    var tooManyFinished = Enumerable.Range(
            0,
            RuntimeMissionLoadSeedParser.MaxFinishedMissions + 1)
        .Select(index => $"finished-{index}")
        .ToArray();
    AssertFormatException(
        SaveJson(coreFinished: tooManyFinished),
        "Too many finished missions were accepted.");

    var tooManyDlc = new Dictionary<string, object>();
    for (var index = 0; index <= RuntimeMissionLoadSeedParser.MaxDlcPartitions; index++)
    {
        tooManyDlc[$"DLC{index}"] = Dlc(1);
    }
    AssertFormatException(
        SaveJson(dlc: tooManyDlc),
        "Too many DLC partitions were accepted.");
}

static void AssertCheckedDateArithmetic()
{
    var seed = RuntimeMissionLoadSeedParser.Parse(SaveJson(
        dlc: new Dictionary<string, object>
        {
            ["DLC1"] = Dlc(
                saveDate: -1,
                buckets: new Dictionary<string, object[]>
                {
                    ["1"] = new[]
                    {
                        Task("overflow", Array.Empty<bool>(), Array.Empty<int[]>()),
                    },
                }),
        }));
    AssertThrows<OverflowException>(
        () => RuntimeMissionLoadSeedParser.SelectAndMerge(
            seed,
            int.MaxValue,
            new HashSet<string>(new[] { "DLC1" }, StringComparer.Ordinal)),
        "A shifted bucket overflow was accepted.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => RuntimeMissionLoadSeedParser.SelectAndMerge(
            seed,
            -1,
            new HashSet<string>(StringComparer.Ordinal)),
        "A negative current date was accepted.");
}

static void AssertOptionalRealSaveFixture()
{
    var fixturePath = Environment.GetEnvironmentVariable("MYSTIA_MISSION_SAVE_FIXTURE");
    if (string.IsNullOrWhiteSpace(fixturePath)) return;
    if (!File.Exists(fixturePath))
    {
        throw new FileNotFoundException("The configured real mission save fixture was not found.", fixturePath);
    }

    var seed = RuntimeMissionLoadSeedParser.Parse(File.ReadAllText(fixturePath, Encoding.UTF8));
    AssertEqual(14, seed.TrackingMissionCount, "The supplied real save tracking count changed.");
    AssertEqual(98, seed.FinishedMissionCount, "The supplied real save finished count changed.");
    var selectedLabels = seed.DlcPartitions
        .Select(partition => partition.Label)
        .ToHashSet(StringComparer.Ordinal);
    var selection = RuntimeMissionLoadSeedParser.SelectAndMerge(
        seed,
        currentDate: 55,
        selectedLabels);
    AssertEqual(14, selection.Tasks.Count, "The supplied real save lost a selected task.");
    AssertEqual(2, selection.Buckets.Count, "The supplied real save merged to an unexpected bucket count.");
    AssertTrue(
        selection.Tasks.Any(task =>
            task.Source.Label == "Kizuna_Akyuu_LV1_Upgrade_002_Mission"),
        "The supplied real save lost the Akyuu ServeInWork sample.");
    AssertTrue(
        selection.Tasks.Any(task =>
            task.Source.Label == "Kizuna_Cirno_LV1_Upgrade_Mission_002"),
        "The supplied real save lost the Cirno ServeInWork sample.");
}

static object Task(string label, object finishStates, object conditionData)
{
    return new
    {
        missionLabel = label,
        conditionFinishStates = finishStates,
        conditionData,
    };
}

static object Dlc(
    int saveDate,
    Dictionary<string, object[]>? buckets = null,
    string[]? finished = null)
{
    return new
    {
        dlcSaveDate = saveDate,
        allTrackingMissions = buckets ?? new Dictionary<string, object[]>(),
        finishedMissions = finished ?? Array.Empty<string>(),
    };
}

static string SaveJson(
    string fileVersion = "BetaV102",
    object? day = null,
    Dictionary<string, object[]>? coreBuckets = null,
    string[]? coreFinished = null,
    Dictionary<string, object>? dlc = null)
{
    return JsonSerializer.Serialize(new
    {
        fileVersion,
        playerPartial = new
        {
            gameDate = new
            {
                day = day ?? 20,
            },
        },
        schedulerPartial = new
        {
            allTrackingMissions = coreBuckets ?? new Dictionary<string, object[]>(),
            finishedMissions = coreFinished ?? Array.Empty<string>(),
        },
        schedulerPartialDLC = dlc ?? new Dictionary<string, object>(),
    });
}

static void AssertFormatException(string json, string message)
{
    AssertThrows<FormatException>(
        () => RuntimeMissionLoadSeedParser.Parse(json),
        message);
}

static void AssertSequenceEqual<T>(
    IEnumerable<T> expected,
    IEnumerable<T> actual,
    string message)
{
    var expectedArray = expected.ToArray();
    var actualArray = actual.ToArray();
    if (!expectedArray.SequenceEqual(actualArray))
    {
        throw new InvalidOperationException(
            $"{message} Expected [{string.Join(", ", expectedArray)}], "
            + $"actual [{string.Join(", ", actualArray)}].");
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected {expected}, actual {actual}.");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool condition, string message)
{
    AssertTrue(!condition, message);
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
