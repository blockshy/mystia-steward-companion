using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeMissionLoadTrackedTask(
    string SourcePartition,
    bool SourceIsCore,
    int SourceBucket,
    int SourceOrdinal,
    string Label,
    int FinishStateCount,
    int TrueFinishStateCount,
    int ConditionDataCount);

internal sealed record RuntimeMissionLoadFinishedMission(
    string SourcePartition,
    bool SourceIsCore,
    int SourceOrdinal,
    string Label);

internal sealed record RuntimeMissionLoadBucket(
    int SourceBucket,
    IReadOnlyList<RuntimeMissionLoadTrackedTask> Tasks);

internal sealed record RuntimeMissionLoadPartition(
    string Label,
    bool IsCore,
    int? DlcSaveDate,
    IReadOnlyList<RuntimeMissionLoadBucket> Buckets,
    IReadOnlyList<RuntimeMissionLoadFinishedMission> FinishedMissions);

internal sealed record RuntimeMissionLoadSeed(
    string FileVersion,
    int SavedGameDay,
    RuntimeMissionLoadPartition Core,
    IReadOnlyList<RuntimeMissionLoadPartition> DlcPartitions,
    int TrackingMissionCount,
    int FinishedMissionCount);

internal sealed record RuntimeMissionLoadSelectedTask(
    int MergedBucket,
    RuntimeMissionLoadTrackedTask Source);

internal sealed record RuntimeMissionLoadMergedBucket(
    int Bucket,
    IReadOnlyList<RuntimeMissionLoadSelectedTask> Tasks);

internal sealed record RuntimeMissionLoadSelection(
    int SavedGameDay,
    int CurrentDate,
    IReadOnlyList<string> SelectedDlcPartitions,
    IReadOnlyList<RuntimeMissionLoadMergedBucket> Buckets,
    IReadOnlyList<RuntimeMissionLoadSelectedTask> Tasks,
    IReadOnlyList<string> FinishedMissionLabels);

internal static class RuntimeMissionLoadSeedParser
{
    internal const int MaxJsonBytes = 32 * 1024 * 1024;
    internal const int MaxDlcPartitions = 64;
    internal const int MaxBucketsPerPartition = 2048;
    internal const int MaxTrackingMissions = 4096;
    internal const int MaxConditionEntriesPerTask = 256;
    internal const int MaxConditionValuesPerEntry = 256;
    internal const int MaxFinishedMissions = 20000;
    internal const int MaxLabelLength = 512;
    internal const int MaxPartitionLabelLength = 128;
    internal const int MaxFileVersionLength = 128;

    private const string CorePartitionLabel = "CORE";

    public static RuntimeMissionLoadSeed Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ValidateJsonSize(json);

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            var root = RequireObject(document.RootElement, "root");
            var fileVersion = ReadBoundedString(
                RequireProperty(root, "fileVersion", "root"),
                "root.fileVersion",
                MaxFileVersionLength);
            var playerPartial = RequireObject(
                RequireProperty(root, "playerPartial", "root"),
                "root.playerPartial");
            var gameDate = RequireObject(
                RequireProperty(playerPartial, "gameDate", "root.playerPartial"),
                "root.playerPartial.gameDate");
            var savedGameDay = ReadInt32(
                RequireProperty(gameDate, "day", "root.playerPartial.gameDate"),
                "root.playerPartial.gameDate.day");
            if (savedGameDay < 0)
            {
                throw Invalid("root.playerPartial.gameDate.day must be non-negative.");
            }

            var seenTrackingLabels = new HashSet<string>(StringComparer.Ordinal);
            var totalTrackingMissions = 0;
            var totalFinishedMissions = 0;

            var schedulerPartial = RequireObject(
                RequireProperty(root, "schedulerPartial", "root"),
                "root.schedulerPartial");
            var core = ParsePartition(
                schedulerPartial,
                CorePartitionLabel,
                isCore: true,
                dlcSaveDate: null,
                seenTrackingLabels,
                ref totalTrackingMissions,
                ref totalFinishedMissions);

            var schedulerPartialDlc = RequireObject(
                RequireProperty(root, "schedulerPartialDLC", "root"),
                "root.schedulerPartialDLC");
            var dlcPartitions = new List<RuntimeMissionLoadPartition>();
            var seenPartitionLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in schedulerPartialDlc.EnumerateObject())
            {
                if (dlcPartitions.Count >= MaxDlcPartitions)
                {
                    throw Invalid($"root.schedulerPartialDLC exceeds {MaxDlcPartitions} partitions.");
                }

                var partitionLabel = ValidateBoundedText(
                    property.Name,
                    "root.schedulerPartialDLC partition label",
                    MaxPartitionLabelLength);
                if (!seenPartitionLabels.Add(partitionLabel))
                {
                    throw Invalid($"root.schedulerPartialDLC contains duplicate partition '{partitionLabel}'.");
                }

                var partitionObject = RequireObject(
                    property.Value,
                    $"root.schedulerPartialDLC.{partitionLabel}");
                var dlcSaveDate = ReadInt32(
                    RequireProperty(
                        partitionObject,
                        "dlcSaveDate",
                        $"root.schedulerPartialDLC.{partitionLabel}"),
                    $"root.schedulerPartialDLC.{partitionLabel}.dlcSaveDate");
                if (dlcSaveDate < -1)
                {
                    throw Invalid(
                        $"root.schedulerPartialDLC.{partitionLabel}.dlcSaveDate must be -1 or non-negative.");
                }

                dlcPartitions.Add(ParsePartition(
                    partitionObject,
                    partitionLabel,
                    isCore: false,
                    dlcSaveDate,
                    seenTrackingLabels,
                    ref totalTrackingMissions,
                    ref totalFinishedMissions));
            }

            return new RuntimeMissionLoadSeed(
                fileVersion,
                savedGameDay,
                core,
                dlcPartitions.ToArray(),
                totalTrackingMissions,
                totalFinishedMissions);
        }
        catch (JsonException ex)
        {
            throw Invalid("Mission load seed JSON is invalid.", ex);
        }
    }

    public static RuntimeMissionLoadSelection SelectAndMerge(
        RuntimeMissionLoadSeed seed,
        int currentDate,
        IReadOnlySet<string> selectedDlcLabels)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(selectedDlcLabels);
        if (currentDate < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentDate), "Current date must be non-negative.");
        }

        var selectedLabels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in selectedDlcLabels)
        {
            selectedLabels.Add(ValidateBoundedText(
                label,
                nameof(selectedDlcLabels),
                MaxPartitionLabelLength));
        }

        var mergedByBucket = new Dictionary<int, List<RuntimeMissionLoadSelectedTask>>();
        var bucketOrder = new List<int>();
        var selectedTasks = new List<RuntimeMissionLoadSelectedTask>();
        var finishedLabels = new List<string>();
        var includedDlcPartitions = new List<string>();

        MergePartition(
            seed.Core,
            dateShift: 0,
            mergedByBucket,
            bucketOrder,
            selectedTasks,
            finishedLabels);

        foreach (var partition in seed.DlcPartitions)
        {
            if (!selectedLabels.Contains(partition.Label))
            {
                continue;
            }

            var savedDate = partition.DlcSaveDate
                ?? throw new InvalidOperationException(
                    $"DLC partition '{partition.Label}' has no saved date.");
            var normalizedSavedDate = savedDate == -1 ? 0 : savedDate;
            var dateShift = checked(currentDate - normalizedSavedDate);
            MergePartition(
                partition,
                dateShift,
                mergedByBucket,
                bucketOrder,
                selectedTasks,
                finishedLabels);
            includedDlcPartitions.Add(partition.Label);
        }

        var buckets = bucketOrder
            .Select(bucket => new RuntimeMissionLoadMergedBucket(
                bucket,
                mergedByBucket[bucket].ToArray()))
            .ToArray();
        return new RuntimeMissionLoadSelection(
            seed.SavedGameDay,
            currentDate,
            includedDlcPartitions.ToArray(),
            buckets,
            selectedTasks.ToArray(),
            finishedLabels.ToArray());
    }

    private static RuntimeMissionLoadPartition ParsePartition(
        JsonElement partitionObject,
        string partitionLabel,
        bool isCore,
        int? dlcSaveDate,
        HashSet<string> seenTrackingLabels,
        ref int totalTrackingMissions,
        ref int totalFinishedMissions)
    {
        var path = isCore
            ? "root.schedulerPartial"
            : $"root.schedulerPartialDLC.{partitionLabel}";
        var trackingObject = RequireObject(
            RequireProperty(partitionObject, "allTrackingMissions", path),
            $"{path}.allTrackingMissions");
        var buckets = new List<RuntimeMissionLoadBucket>();
        var seenBuckets = new HashSet<int>();
        foreach (var bucketProperty in trackingObject.EnumerateObject())
        {
            if (buckets.Count >= MaxBucketsPerPartition)
            {
                throw Invalid(
                    $"{path}.allTrackingMissions exceeds {MaxBucketsPerPartition} buckets.");
            }

            var bucket = ParseBucketKey(bucketProperty.Name, $"{path}.allTrackingMissions");
            if (!seenBuckets.Add(bucket))
            {
                throw Invalid($"{path}.allTrackingMissions contains duplicate bucket {bucket}.");
            }

            var taskArray = RequireArray(
                bucketProperty.Value,
                $"{path}.allTrackingMissions.{bucketProperty.Name}");
            var tasks = new List<RuntimeMissionLoadTrackedTask>();
            var ordinal = 0;
            foreach (var taskElement in taskArray.EnumerateArray())
            {
                totalTrackingMissions = checked(totalTrackingMissions + 1);
                if (totalTrackingMissions > MaxTrackingMissions)
                {
                    throw Invalid($"Tracking mission count exceeds {MaxTrackingMissions}.");
                }

                var taskPath = $"{path}.allTrackingMissions.{bucketProperty.Name}[{ordinal}]";
                var taskObject = RequireObject(taskElement, taskPath);
                var label = ReadBoundedString(
                    RequireProperty(taskObject, "missionLabel", taskPath),
                    $"{taskPath}.missionLabel",
                    MaxLabelLength);
                if (!seenTrackingLabels.Add(label))
                {
                    throw Invalid($"Tracking mission label '{label}' is not globally unique.");
                }

                var finishStates = RequireArray(
                    RequireProperty(taskObject, "conditionFinishStates", taskPath),
                    $"{taskPath}.conditionFinishStates");
                if (finishStates.GetArrayLength() > MaxConditionEntriesPerTask)
                {
                    throw Invalid(
                        $"{taskPath}.conditionFinishStates exceeds {MaxConditionEntriesPerTask} entries.");
                }

                var trueStateCount = 0;
                foreach (var state in finishStates.EnumerateArray())
                {
                    if (state.ValueKind != JsonValueKind.True
                        && state.ValueKind != JsonValueKind.False)
                    {
                        throw Invalid($"{taskPath}.conditionFinishStates must contain only booleans.");
                    }

                    if (state.GetBoolean())
                    {
                        trueStateCount = checked(trueStateCount + 1);
                    }
                }

                var conditionData = RequireArray(
                    RequireProperty(taskObject, "conditionData", taskPath),
                    $"{taskPath}.conditionData");
                if (conditionData.GetArrayLength() > MaxConditionEntriesPerTask)
                {
                    throw Invalid(
                        $"{taskPath}.conditionData exceeds {MaxConditionEntriesPerTask} entries.");
                }

                var conditionDataOrdinal = 0;
                foreach (var conditionEntry in conditionData.EnumerateArray())
                {
                    var entryPath = $"{taskPath}.conditionData[{conditionDataOrdinal}]";
                    var values = RequireArray(conditionEntry, entryPath);
                    if (values.GetArrayLength() > MaxConditionValuesPerEntry)
                    {
                        throw Invalid(
                            $"{entryPath} exceeds {MaxConditionValuesPerEntry} values.");
                    }

                    foreach (var value in values.EnumerateArray())
                    {
                        _ = ReadInt32(value, entryPath);
                    }
                    conditionDataOrdinal = checked(conditionDataOrdinal + 1);
                }

                tasks.Add(new RuntimeMissionLoadTrackedTask(
                    partitionLabel,
                    isCore,
                    bucket,
                    ordinal,
                    label,
                    finishStates.GetArrayLength(),
                    trueStateCount,
                    conditionData.GetArrayLength()));
                ordinal = checked(ordinal + 1);
            }

            buckets.Add(new RuntimeMissionLoadBucket(bucket, tasks.ToArray()));
        }

        var finishedArray = RequireArray(
            RequireProperty(partitionObject, "finishedMissions", path),
            $"{path}.finishedMissions");
        var finishedMissions = new List<RuntimeMissionLoadFinishedMission>();
        var finishedOrdinal = 0;
        foreach (var finishedElement in finishedArray.EnumerateArray())
        {
            totalFinishedMissions = checked(totalFinishedMissions + 1);
            if (totalFinishedMissions > MaxFinishedMissions)
            {
                throw Invalid($"Finished mission count exceeds {MaxFinishedMissions}.");
            }

            var label = ReadBoundedString(
                finishedElement,
                $"{path}.finishedMissions[{finishedOrdinal}]",
                MaxLabelLength);
            finishedMissions.Add(new RuntimeMissionLoadFinishedMission(
                partitionLabel,
                isCore,
                finishedOrdinal,
                label));
            finishedOrdinal = checked(finishedOrdinal + 1);
        }

        return new RuntimeMissionLoadPartition(
            partitionLabel,
            isCore,
            dlcSaveDate,
            buckets.ToArray(),
            finishedMissions.ToArray());
    }

    private static void MergePartition(
        RuntimeMissionLoadPartition partition,
        int dateShift,
        Dictionary<int, List<RuntimeMissionLoadSelectedTask>> mergedByBucket,
        List<int> bucketOrder,
        List<RuntimeMissionLoadSelectedTask> selectedTasks,
        List<string> finishedLabels)
    {
        foreach (var bucket in partition.Buckets)
        {
            var mergedBucket = bucket.SourceBucket == -1
                ? -1
                : checked(bucket.SourceBucket + dateShift);
            if (mergedBucket < -1)
            {
                throw new OverflowException(
                    $"Partition '{partition.Label}' shifted bucket {bucket.SourceBucket} "
                    + $"to invalid bucket {mergedBucket}.");
            }

            if (!mergedByBucket.TryGetValue(
                    mergedBucket,
                    out var mergedTasks))
            {
                mergedTasks = new List<RuntimeMissionLoadSelectedTask>();
                mergedByBucket.Add(mergedBucket, mergedTasks);
                bucketOrder.Add(mergedBucket);
            }

            foreach (var task in bucket.Tasks)
            {
                var selectedTask = new RuntimeMissionLoadSelectedTask(mergedBucket, task);
                mergedTasks.Add(selectedTask);
                selectedTasks.Add(selectedTask);
            }
        }

        foreach (var finished in partition.FinishedMissions)
        {
            finishedLabels.Add(finished.Label);
        }
    }

    private static int ParseBucketKey(string text, string path)
    {
        if (!int.TryParse(
                text,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var bucket)
            || bucket < -1)
        {
            throw Invalid($"{path} contains invalid bucket key '{text}'.");
        }
        return bucket;
    }

    private static JsonElement RequireProperty(
        JsonElement source,
        string propertyName,
        string path)
    {
        JsonElement value = default;
        var matchCount = 0;
        foreach (var property in source.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal))
            {
                continue;
            }

            value = property.Value;
            matchCount++;
        }

        return matchCount switch
        {
            1 => value,
            0 => throw Invalid($"{path}.{propertyName} is required."),
            _ => throw Invalid($"{path}.{propertyName} is duplicated."),
        };
    }

    private static JsonElement RequireObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{path} must be an object.");
        }
        return value;
    }

    private static JsonElement RequireArray(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{path} must be an array.");
        }
        return value;
    }

    private static int ReadInt32(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw Invalid($"{path} must be a 32-bit integer.");
        }
        return result;
    }

    private static string ReadBoundedString(JsonElement value, string path, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"{path} must be a string.");
        }
        return ValidateBoundedText(value.GetString(), path, maxLength);
    }

    private static string ValidateBoundedText(string? value, string path, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{path} must not be empty.");
        }
        if (value.Length > maxLength)
        {
            throw Invalid($"{path} exceeds {maxLength} characters.");
        }
        return value;
    }

    private static void ValidateJsonSize(string json)
    {
        if (json.Length > MaxJsonBytes)
        {
            throw Invalid($"Mission load seed JSON exceeds {MaxJsonBytes} bytes.");
        }

        int byteCount;
        try
        {
            byteCount = Encoding.UTF8.GetByteCount(json);
        }
        catch (EncoderFallbackException ex)
        {
            throw Invalid("Mission load seed JSON contains invalid UTF-16.", ex);
        }

        if (byteCount > MaxJsonBytes)
        {
            throw Invalid($"Mission load seed JSON exceeds {MaxJsonBytes} bytes.");
        }
    }

    private static FormatException Invalid(string message, Exception? innerException = null)
    {
        return new FormatException(message, innerException);
    }
}
