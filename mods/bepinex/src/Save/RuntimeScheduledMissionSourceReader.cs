using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeScheduledMissionSourceReader
{
    private const string RunTimeSchedulerTypeName = "GameData.RunTime.Common.RunTimeScheduler";
    private const string RunTimePlayerDataTypeName = "GameData.RunTime.Common.RunTimePlayerData";
    private const string RunTimeAlbumTypeName = "GameData.RunTime.Common.RunTimeAlbum";
    private const string SpecialGuestRunTimeDataTypeName =
        "GameData.RunTime.Common.RunTimeAlbum+SpecialGuestRunTimeData";
    private const string GameDateTypeName = "GameData.RunTime.Common.GameDate";
    private const string DataBaseSchedulerTypeName = "GameData.Core.Collections.DataBaseScheduler";
    private const string DataBaseDayTypeName =
        "GameData.Core.Collections.DaySceneUtility.DataBaseDay";
    private const string NpcTypeName =
        "GameData.Core.Collections.DaySceneUtility.Collections.NPC";
    private const string SchedulerNodeTypeName = "GameData.Profile.SchedulerNode";
    private const string SchedulerCharacterTypeName =
        "GameData.Profile.SchedulerNode+Character";
    private const string EventNodeTypeName =
        "GameData.Profile.SchedulerNodeCollection.EventNode";
    private const string MissionNodeTypeName =
        "GameData.Profile.SchedulerNodeCollection.MissionNode";
    private const string TriggerTypeName = "GameData.Profile.SchedulerNode+Trigger";
    private const string TriggerKindTypeName =
        "GameData.Profile.SchedulerNode+Trigger+TriggerType";
    private const string DayTypeName = "GameData.Profile.SchedulerNode+Day";
    private const string DayKindTypeName = "GameData.Profile.SchedulerNode+Day+DayType";
    private const string DayCalculateTypeName =
        "GameData.Profile.SchedulerNode+Day+CalculateType";
    private const string ScheduledEventTypeName =
        "GameData.Profile.SchedulerNode+ScheduledEvent";
    private const string Il2CppDictionaryTypeName =
        "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const string Il2CppListTypeName = "Il2CppSystem.Collections.Generic.List`1";
    private const string Il2CppStringArrayTypeName =
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray";

    private const int PermanentBucket = -1;
    private const int MaxCorrectedDay = 1_000_000;
    private const int MaxPreNodeCount = 4096;

    private static readonly RuntimeScheduledEventDiagnosticLimits HardLimits = new(
        MaxCaptureAttemptsPerStableWindow: 1,
        MaxScheduledBucketCount: 4096,
        MaxEventsPerBucket: 2048,
        MaxScheduledEventCount: 4096,
        MaxPostMissionReferences: 4096,
        MaxFinishedEventCount: 20_000,
        MaxFinishedMissionCount: 20_000,
        MaxLabelLength: 512);
    private static readonly object ShapeRoot = new();
    private static RuntimeShape? _shape;

    public static RuntimeScheduledEventDiagnosticLimits Limits => HardLimits;

    public static bool TryResolve(out string failure)
    {
        lock (ShapeRoot)
        {
            if (_shape != null)
            {
                failure = "";
                return true;
            }

            try
            {
                _shape = RuntimeShape.Resolve();
                failure = "";
                return true;
            }
            catch (Exception ex)
            {
                _shape = null;
                failure = $"reader-shape-unavailable:{DescribeException(ex)}";
                return false;
            }
        }
    }

    public static RuntimeScheduledMissionSourceReadResult ReadFresh(
        RuntimeMissionDiagnosticSnapshot missionSnapshot,
        long dayGeneration,
        RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot)
    {
        ArgumentNullException.ThrowIfNull(missionSnapshot);
        ArgumentNullException.ThrowIfNull(mappedGuestSnapshot);
        RuntimeShape shape;
        lock (ShapeRoot)
        {
            shape = _shape
                ?? throw new InvalidOperationException(
                    "scheduled-mission-source-reader-not-resolved");
        }
        return Read(shape, missionSnapshot, dayGeneration, mappedGuestSnapshot);
    }

    private static RuntimeScheduledMissionSourceReadResult Read(
        RuntimeShape shape,
        RuntimeMissionDiagnosticSnapshot missionSnapshot,
        long dayGeneration,
        RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot)
    {
        var timer = Stopwatch.StartNew();
        if (Environment.CurrentManagedThreadId != missionSnapshot.OwnerThreadId)
        {
            throw new InvalidOperationException("scheduled-event-owner-thread-mismatch");
        }
        if (dayGeneration < 1)
        {
            throw new InvalidOperationException("scheduled-event-day-generation-invalid");
        }

        var missionReport = RuntimeMissionDiagnosticCapture.Report();
        if (missionReport.Summary.Generation != missionSnapshot.Generation
            || missionReport.Summary.ChangeVersion != missionSnapshot.ChangeVersion
            || missionReport.Summary.Phase != RuntimeMissionDiagnosticPhase.Ready
            || !missionReport.Summary.RuntimeAvailable)
        {
            throw new InvalidOperationException("mission-report-changed-before-scheduled-read");
        }

        var activeMissionLabels = ReadActiveMissionLabels(
            missionReport,
            missionSnapshot.ActiveMissionCount);
        var scheduledEvents = shape.ScheduledEvents.GetValue(null)
            ?? throw new InvalidOperationException("scheduled-events-missing");
        var finishedEvents = ReadStringList(
            shape.FinishedEvents.GetValue(null),
            HardLimits.MaxFinishedEventCount,
            "finished-events");
        var finishedMissions = ReadStringList(
            shape.FinishedMissions.GetValue(null),
            HardLimits.MaxFinishedMissionCount,
            "finished-missions");
        var finishedEventLabels = RuntimeScheduledEventDiagnosticBounds.BuildMembershipSet(
            finishedEvents,
            HardLimits.MaxFinishedEventCount,
            "finished-events");
        var finishedMissionLabels = RuntimeScheduledEventDiagnosticBounds.BuildMembershipSet(
            finishedMissions,
            HardLimits.MaxFinishedMissionCount,
            "finished-missions");

        if (!RuntimeConcreteCollectionReader.TryReadDictionaryCount(
                scheduledEvents,
                out var initialBucketCount,
                out var bucketCountFailure))
        {
            throw new InvalidOperationException(
                $"scheduled-events-count-{FormatFailure(bucketCountFailure)}");
        }
        if (initialBucketCount > HardLimits.MaxScheduledBucketCount)
        {
            throw new InvalidOperationException(
                $"scheduled-events-count-overflow:{initialBucketCount}");
        }

        var correctedDay = ReadCorrectedDay(shape);
        var seeds = new List<ScheduledEventSeed>();
        var readBucketCount = 0;
        ReadScheduledBucket(
            scheduledEvents,
            correctedDay,
            "current-day",
            seeds,
            ref readBucketCount);
        ReadScheduledBucket(
            scheduledEvents,
            PermanentBucket,
            "permanent",
            seeds,
            ref readBucketCount);
        if (seeds.Count > HardLimits.MaxScheduledEventCount)
        {
            throw new InvalidOperationException(
                $"scheduled-event-total-overflow:{seeds.Count}");
        }

        var duplicateLabels = seeds
            .GroupBy(seed => seed.Label, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var eligibilityReader = new EligibilityReader(shape, mappedGuestSnapshot);
        var events = new List<RuntimeScheduledEventDiagnosticEntry>(seeds.Count);
        foreach (var seed in seeds)
        {
            events.Add(ReadEventDefinition(
                shape,
                seed,
                finishedEventLabels.Contains(seed.Label),
                duplicateLabels.Contains(seed.Label),
                eligibilityReader));
        }

        var missionReferences = ReadMissionReferences(
            shape,
            events,
            activeMissionLabels,
            finishedMissionLabels);
        if (missionReferences.Count > HardLimits.MaxPostMissionReferences)
        {
            throw new InvalidOperationException(
                $"scheduled-event-post-mission-overflow:{missionReferences.Count}");
        }

        if (!RuntimeConcreteCollectionReader.TryReadDictionaryCount(
                scheduledEvents,
                out var finalBucketCount,
                out var finalBucketCountFailure))
        {
            throw new InvalidOperationException(
                $"scheduled-events-final-count-{FormatFailure(finalBucketCountFailure)}");
        }
        if (finalBucketCount != initialBucketCount)
        {
            throw new InvalidOperationException("scheduled-events-bucket-count-changed");
        }

        var finalFinishedEvents = ReadStringList(
            shape.FinishedEvents.GetValue(null),
            HardLimits.MaxFinishedEventCount,
            "finished-events-final");
        var finalFinishedMissions = ReadStringList(
            shape.FinishedMissions.GetValue(null),
            HardLimits.MaxFinishedMissionCount,
            "finished-missions-final");
        if (!finishedEvents.SequenceEqual(finalFinishedEvents, StringComparer.Ordinal)
            || !finishedMissions.SequenceEqual(finalFinishedMissions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("finished-labels-changed-during-capture");
        }
        if (ReadCorrectedDay(shape) != correctedDay)
        {
            throw new InvalidOperationException(
                "runtime-player-corrected-day-changed-during-capture");
        }

        timer.Stop();
        var invalidEventCount = events.Count(entry =>
            string.Equals(entry.Disposition, "invalid", StringComparison.Ordinal));
        var invalidEligibilityCount = events.Count(entry =>
            string.Equals(
                entry.Eligibility?.Disposition,
                "invalid",
                StringComparison.Ordinal));
        var invalidMissionCount = missionReferences.Count(entry =>
            string.Equals(entry.Disposition, "invalid", StringComparison.Ordinal));
        var complete = invalidEventCount == 0
            && invalidEligibilityCount == 0
            && invalidMissionCount == 0;
        var error = complete
            ? ""
            : $"invalid-events={invalidEventCount}; "
                + $"invalid-eligibility={invalidEligibilityCount}; "
                + $"invalid-mission-references={invalidMissionCount}";
        return new RuntimeScheduledMissionSourceReadResult(
            Complete: complete,
            SourceMissionChangeVersion: missionSnapshot.ChangeVersion,
            CorrectedDay: correctedDay,
            ScheduledBucketCount: initialBucketCount,
            ReadBucketCount: readBucketCount,
            FinishedEvents: finishedEvents,
            FinishedMissions: finishedMissions,
            Events: events,
            MissionReferences: missionReferences,
            CaptureElapsedMilliseconds: timer.ElapsedMilliseconds,
            Error: error);
    }

    private static HashSet<string> ReadActiveMissionLabels(
        RuntimeMissionDiagnosticReport report,
        int expectedActiveCount)
    {
        var activeLabels = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < report.Tasks.Count; index++)
        {
            var task = report.Tasks[index];
            if (!task.Active) continue;
            var label = RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(
                task.Label,
                HardLimits.MaxLabelLength,
                "active-mission",
                index);
            if (!activeLabels.Add(label))
            {
                throw new InvalidOperationException(
                    $"duplicate-active-mission-label:{label}");
            }
        }

        if (activeLabels.Count != expectedActiveCount)
        {
            throw new InvalidOperationException(
                "active-mission-count-does-not-match-tracked-diagnostic");
        }
        return activeLabels;
    }

    private static int ReadCorrectedDay(RuntimeShape shape)
    {
        var day = shape.GetDay.Invoke(null, Array.Empty<object?>());
        if (day == null || day.GetType() != shape.GameDateType)
        {
            throw new InvalidOperationException("runtime-player-day-type-mismatch");
        }
        if (shape.CorrectedDay.GetValue(day) is not int correctedDay
            || correctedDay < 0
            || correctedDay > MaxCorrectedDay)
        {
            throw new InvalidOperationException("runtime-player-corrected-day-invalid");
        }
        return correctedDay;
    }

    private static void ReadScheduledBucket(
        object scheduledEvents,
        int bucket,
        string bucketSource,
        ICollection<ScheduledEventSeed> destination,
        ref int readBucketCount)
    {
        if (!RuntimeConcreteCollectionReader.TryGetDictionaryValue(
                scheduledEvents,
                bucket,
                out var rawList,
                out var found,
                out var lookupFailure))
        {
            throw new InvalidOperationException(
                $"scheduled-events-bucket-{bucket}-{FormatFailure(lookupFailure)}");
        }
        if (!found) return;
        if (!RuntimeConcreteCollectionReader.TryReadList(
                rawList,
                out var values,
                out var listFailure))
        {
            throw new InvalidOperationException(
                $"scheduled-events-bucket-{bucket}-list-{FormatFailure(listFailure)}");
        }
        if (values.Count > HardLimits.MaxEventsPerBucket)
        {
            throw new InvalidOperationException(
                $"scheduled-events-bucket-{bucket}-overflow:{values.Count}");
        }

        readBucketCount++;
        for (var index = 0; index < values.Count; index++)
        {
            var label = RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(
                values[index],
                HardLimits.MaxLabelLength,
                $"scheduled-events-bucket-{bucket}",
                index);
            destination.Add(new ScheduledEventSeed(label, bucket, bucketSource, index));
        }
    }

    private static RuntimeScheduledEventDiagnosticEntry ReadEventDefinition(
        RuntimeShape shape,
        ScheduledEventSeed seed,
        bool finished,
        bool duplicated,
        EligibilityReader eligibilityReader)
    {
        if (duplicated)
        {
            return InvalidEvent(seed, "duplicate-scheduled-event-label", finished);
        }

        bool? definitionExists = null;
        try
        {
            if (shape.TargetNodeExists.Invoke(
                    null,
                    new object?[] { seed.Label }) is not bool exists)
            {
                return InvalidEvent(seed, "target-node-exists-non-boolean", finished);
            }
            definitionExists = exists;
            if (!exists)
            {
                return InvalidEvent(seed, "event-label-not-found", finished, definitionExists: false);
            }

            var eventNode = shape.RefEvent.Invoke(null, new object?[] { seed.Label });
            if (eventNode == null || eventNode.GetType() != shape.EventNodeType)
            {
                return InvalidEvent(seed, "ref-event-type-mismatch", finished);
            }
            if (shape.Label.GetValue(eventNode) is not string resolvedLabel
                || !string.Equals(resolvedLabel, seed.Label, StringComparison.Ordinal))
            {
                return InvalidEvent(seed, "ref-event-label-mismatch", finished);
            }

            var scheduledEvent = shape.ScheduledEvent.GetValue(eventNode);
            if (scheduledEvent == null
                || scheduledEvent.GetType() != shape.ScheduledEventType)
            {
                return InvalidEvent(seed, "scheduled-event-type-mismatch", finished);
            }
            var trigger = shape.Trigger.GetValue(scheduledEvent);
            if (trigger == null || trigger.GetType() != shape.TriggerType)
            {
                return InvalidEvent(seed, "event-trigger-type-mismatch", finished);
            }
            var triggerDiagnostic = ReadTrigger(shape, trigger, seed);
            var postMissions = ReadPostMissionArray(
                shape.PostMissions.GetValue(eventNode),
                seed,
                "postMissions");
            var postMissionsAfterPerformance = ReadPostMissionArray(
                shape.PostMissionsAfterPerformance.GetValue(eventNode),
                seed,
                "postMissionsAfterPerformance");
            var eligibility = ReadEligibility(
                eligibilityReader,
                triggerDiagnostic,
                finished);
            return new RuntimeScheduledEventDiagnosticEntry(
                seed.Label,
                seed.Bucket,
                seed.BucketSource,
                seed.Ordinal,
                DefinitionExists: definitionExists,
                DefinitionAvailable: true,
                DefinitionStatus: "available",
                Finished: finished,
                Disposition: finished ? "skipped" : "candidate",
                Reason: finished ? "event-finished" : "",
                Trigger: triggerDiagnostic,
                Eligibility: eligibility,
                PostMissions: postMissions,
                PostMissionsAfterPerformance: postMissionsAfterPerformance);
        }
        catch (Exception ex)
        {
            return InvalidEvent(
                seed,
                $"event-definition-read-failed:{DescribeException(ex)}",
                finished,
                definitionExists);
        }
    }

    private static RuntimeScheduledEventEligibilityDiagnostic ReadEligibility(
        EligibilityReader eligibilityReader,
        RuntimeScheduledEventTriggerDiagnostic trigger,
        bool eventFinished)
    {
        try
        {
            return eligibilityReader.Read(trigger, eventFinished);
        }
        catch (Exception ex)
        {
            return RuntimeScheduledEventEligibility.Invalid(
                $"eligibility-read-failed:{DescribeException(ex)}");
        }
    }

    private static RuntimeScheduledEventTriggerDiagnostic ReadTrigger(
        RuntimeShape shape,
        object trigger,
        ScheduledEventSeed seed)
    {
        var rawTriggerType = shape.TriggerKind.GetValue(trigger);
        if (rawTriggerType == null || rawTriggerType.GetType() != shape.TriggerKindType)
        {
            throw new InvalidOperationException("trigger-kind-type-mismatch");
        }
        var rawTriggerId = shape.TriggerId.GetValue(trigger);
        var triggerId = RuntimeScheduledEventDiagnosticIdentity.ReadOptionalIdentifier(
            rawTriggerId,
            HardLimits.MaxLabelLength,
            $"scheduled-event-trigger-id-bucket-{seed.Bucket}",
            seed.Ordinal);
        var time = shape.TriggerTime.GetValue(trigger);
        if (time == null || time.GetType() != shape.DayType)
        {
            throw new InvalidOperationException("trigger-time-type-mismatch");
        }
        var rawDayType = shape.TimeDayKind.GetValue(time);
        var rawCalculateType = shape.TimeCalculateKind.GetValue(time);
        if (rawDayType == null || rawDayType.GetType() != shape.DayKindType)
        {
            throw new InvalidOperationException("trigger-time-day-kind-type-mismatch");
        }
        if (rawCalculateType == null
            || rawCalculateType.GetType() != shape.DayCalculateType)
        {
            throw new InvalidOperationException("trigger-time-calculate-kind-type-mismatch");
        }
        if (shape.TimeDay.GetValue(time) is not int day)
        {
            throw new InvalidOperationException("trigger-time-day-type-mismatch");
        }
        if (shape.TimeDayRange.GetValue(time) is not Vector2Int range)
        {
            throw new InvalidOperationException("trigger-time-range-type-mismatch");
        }

        var triggerKind = Convert.ToInt32(rawTriggerType, CultureInfo.InvariantCulture);
        var dayKind = Convert.ToInt32(rawDayType, CultureInfo.InvariantCulture);
        var calculateKind = Convert.ToInt32(rawCalculateType, CultureInfo.InvariantCulture);
        return new RuntimeScheduledEventTriggerDiagnostic(
            triggerKind,
            Enum.GetName(shape.TriggerKindType, rawTriggerType) ?? "",
            triggerId,
            dayKind,
            Enum.GetName(shape.DayKindType, rawDayType) ?? "",
            calculateKind,
            Enum.GetName(shape.DayCalculateType, rawCalculateType) ?? "",
            day,
            range.x,
            range.y);
    }

    private static IReadOnlyList<string> ReadPostMissionArray(
        object? rawArray,
        ScheduledEventSeed seed,
        string source)
    {
        var sourceIdentity =
            $"scheduled-event-{source}-bucket-{seed.Bucket}-event-{seed.Ordinal}";
        if (!RuntimeConcreteCollectionReader.TryReadStringArray(
                rawArray,
                out var values,
                out var failure))
        {
            throw new InvalidOperationException(
                $"{sourceIdentity}-{FormatFailure(failure)}");
        }
        if (values.Count > HardLimits.MaxPostMissionReferences)
        {
            throw new InvalidOperationException(
                $"{sourceIdentity}-overflow:{values.Count}");
        }

        var result = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            result[index] = RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(
                values[index],
                HardLimits.MaxLabelLength,
                sourceIdentity,
                index);
        }
        return result;
    }

    private static IReadOnlyList<RuntimeScheduledEventMissionReferenceDiagnostic>
        ReadMissionReferences(
            RuntimeShape shape,
            IReadOnlyList<RuntimeScheduledEventDiagnosticEntry> events,
            IReadOnlySet<string> activeMissionLabels,
            IReadOnlySet<string> finishedMissionLabels)
    {
        var definitions =
            new Dictionary<string, RuntimeMissionDefinitionDiagnosticReadResult>(
                StringComparer.Ordinal);
        var startMetadata =
            new Dictionary<string, MissionStartMetadataReadResult>(
                StringComparer.Ordinal);
        var result = new List<RuntimeScheduledEventMissionReferenceDiagnostic>();
        foreach (var scheduledEvent in events)
        {
            AddMissionReferences(
                scheduledEvent,
                scheduledEvent.PostMissions,
                "postMissions",
                shape,
                activeMissionLabels,
                finishedMissionLabels,
                definitions,
                startMetadata,
                result);
            AddMissionReferences(
                scheduledEvent,
                scheduledEvent.PostMissionsAfterPerformance,
                "postMissionsAfterPerformance",
                shape,
                activeMissionLabels,
                finishedMissionLabels,
                definitions,
                startMetadata,
                result);
        }
        return result;
    }

    private static void AddMissionReferences(
        RuntimeScheduledEventDiagnosticEntry scheduledEvent,
        IReadOnlyList<string> labels,
        string source,
        RuntimeShape shape,
        IReadOnlySet<string> activeMissionLabels,
        IReadOnlySet<string> finishedMissionLabels,
        IDictionary<string, RuntimeMissionDefinitionDiagnosticReadResult> definitions,
        IDictionary<string, MissionStartMetadataReadResult> startMetadata,
        ICollection<RuntimeScheduledEventMissionReferenceDiagnostic> destination)
    {
        for (var index = 0; index < labels.Count; index++)
        {
            if (destination.Count >= HardLimits.MaxPostMissionReferences)
            {
                throw new InvalidOperationException(
                    $"post-mission-reference-overflow:{destination.Count + 1}");
            }

            var label = labels[index];
            if (!definitions.TryGetValue(label, out var definitionRead))
            {
                definitionRead = RuntimeMissionDefinitionDiagnosticReader.Read(label);
                definitions.Add(label, definitionRead);
            }
            if (!startMetadata.TryGetValue(label, out var startMetadataRead))
            {
                startMetadataRead = ReadMissionStartMetadata(shape, label);
                startMetadata.Add(label, startMetadataRead);
            }

            var definition = definitionRead.Definition;
            var definitionAvailable = definitionRead.Success
                && definition != null
                && string.Equals(definition.Label, label, StringComparison.Ordinal)
                && startMetadataRead.Success;
            bool? definitionExists = definitionRead.Success
                ? true
                : string.Equals(
                    definitionRead.Failure,
                    "mission-label-not-found",
                    StringComparison.Ordinal)
                    ? false
                    : null;
            var active = activeMissionLabels.Contains(label);
            var finished = finishedMissionLabels.Contains(label);
            var disposition = "candidate";
            var reason = "";
            if (!definitionAvailable)
            {
                disposition = "invalid";
                reason = definitionRead.Success
                    ? definition == null
                        || !string.Equals(
                            definition.Label,
                            label,
                            StringComparison.Ordinal)
                            ? "mission-definition-label-mismatch"
                            : startMetadataRead.Failure
                    : definitionRead.Failure;
            }
            else if (active
                && finished
                && !startMetadataRead.LoopedMission)
            {
                disposition = "invalid";
                reason = "mission-active-and-finished";
            }
            else if (!scheduledEvent.DefinitionAvailable)
            {
                disposition = "invalid";
                reason = "source-event-definition-invalid";
            }
            else if (scheduledEvent.Finished)
            {
                disposition = "skipped";
                reason = "source-event-finished";
            }
            else if (active)
            {
                disposition = "skipped";
                reason = "mission-active";
            }
            else if (finished && !startMetadataRead.LoopedMission)
            {
                disposition = "skipped";
                reason = "mission-finished";
            }

            destination.Add(new RuntimeScheduledEventMissionReferenceDiagnostic(
                scheduledEvent.Label,
                scheduledEvent.Bucket,
                source,
                index,
                label,
                definitionExists,
                definitionAvailable,
                definitionAvailable ? "available" : reason,
                definition?.Title ?? "",
                definition?.TitleStatus ?? "unavailable",
                definition?.HasReceiver ?? false,
                definition?.Receiver ?? "",
                definition?.ConditionCount ?? 0,
                active,
                finished,
                scheduledEvent.Eligibility?.Disposition ?? "invalid",
                scheduledEvent.Eligibility?.Reason
                    ?? "source-event-eligibility-missing",
                disposition,
                reason,
                startMetadataRead.PreNodes,
                startMetadataRead.LoopedMission));
        }
    }

    private static MissionStartMetadataReadResult ReadMissionStartMetadata(
        RuntimeShape shape,
        string label)
    {
        try
        {
            if (shape.TargetNodeExists.Invoke(
                    null,
                    new object?[] { label }) is not bool exists)
            {
                throw new InvalidOperationException(
                    "mission-target-node-exists-non-boolean");
            }
            if (!exists)
            {
                return MissionStartMetadataReadResult.Failed(
                    "mission-label-not-found");
            }

            var mission = shape.RefMission.Invoke(null, new object?[] { label });
            if (mission == null || mission.GetType() != shape.MissionNodeType)
            {
                throw new InvalidOperationException(
                    "ref-mission-type-mismatch");
            }
            if (shape.Label.GetValue(mission) is not string resolvedLabel
                || !string.Equals(resolvedLabel, label, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "ref-mission-label-mismatch");
            }
            if (!RuntimeConcreteCollectionReader.TryReadStringArray(
                    shape.PreNodes.GetValue(mission),
                    out var rawPreNodes,
                    out var preNodesFailure))
            {
                throw new InvalidOperationException(
                    $"mission-pre-nodes-{FormatFailure(preNodesFailure)}");
            }
            RuntimeScheduledEventDiagnosticBounds.ValidateCount(
                rawPreNodes.Count,
                MaxPreNodeCount,
                "mission-pre-nodes");
            var preNodes = new string[rawPreNodes.Count];
            for (var index = 0; index < rawPreNodes.Count; index++)
            {
                preNodes[index] =
                    RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(
                        rawPreNodes[index],
                        HardLimits.MaxLabelLength,
                        $"mission-{label}-preNodes",
                        index);
            }
            if (preNodes.Distinct(StringComparer.Ordinal).Count()
                != preNodes.Length)
            {
                throw new InvalidOperationException(
                    "mission-pre-nodes-duplicate");
            }
            if (shape.LoopedMission.GetValue(mission) is not bool loopedMission)
            {
                throw new InvalidOperationException(
                    "mission-looped-mission-non-boolean");
            }
            return new MissionStartMetadataReadResult(
                true,
                preNodes,
                loopedMission,
                "");
        }
        catch (Exception ex)
        {
            return MissionStartMetadataReadResult.Failed(
                $"mission-start-metadata-read-failed:{DescribeException(ex)}");
        }
    }

    private static RuntimeScheduledEventDiagnosticEntry InvalidEvent(
        ScheduledEventSeed seed,
        string reason,
        bool finished,
        bool? definitionExists = null)
    {
        return new RuntimeScheduledEventDiagnosticEntry(
            seed.Label,
            seed.Bucket,
            seed.BucketSource,
            seed.Ordinal,
            definitionExists,
            DefinitionAvailable: false,
            DefinitionStatus: reason,
            Finished: finished,
            Disposition: "invalid",
            Reason: reason,
            Trigger: null,
            Eligibility: null,
            PostMissions: Array.Empty<string>(),
            PostMissionsAfterPerformance: Array.Empty<string>());
    }

    private static IReadOnlyList<string> ReadStringList(
        object? rawList,
        int maximumCount,
        string source)
    {
        if (!RuntimeConcreteCollectionReader.TryReadList(
                rawList,
                out var values,
                out var failure))
        {
            throw new InvalidOperationException(
                $"{source}-list-{FormatFailure(failure)}");
        }
        RuntimeScheduledEventDiagnosticBounds.ValidateCount(
            values.Count,
            maximumCount,
            source);

        var result = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            result[index] = RuntimeScheduledEventDiagnosticIdentity.ReadHistoryLabel(
                values[index],
                HardLimits.MaxLabelLength,
                source,
                index);
        }
        return result;
    }

    private static string FormatFailure(RuntimeCollectionReadFailure failure)
    {
        return failure switch
        {
            RuntimeCollectionReadFailure.None => "none",
            RuntimeCollectionReadFailure.Missing => "missing",
            RuntimeCollectionReadFailure.UnsupportedShape => "unsupported-shape",
            RuntimeCollectionReadFailure.InvocationFailed => "invocation-failed",
            RuntimeCollectionReadFailure.CountMismatch => "count-mismatch",
            RuntimeCollectionReadFailure.ElementTypeMismatch => "element-type-mismatch",
            _ => "unknown",
        };
    }

    private static string DescribeException(Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException { InnerException: not null })
        {
            current = current.InnerException;
        }
        return $"{current.GetType().Name}:{current.Message}";
    }

    private sealed record ScheduledEventSeed(
        string Label,
        int Bucket,
        string BucketSource,
        int Ordinal);

    private sealed record MissionStartMetadataReadResult(
        bool Success,
        IReadOnlyList<string> PreNodes,
        bool LoopedMission,
        string Failure)
    {
        public static MissionStartMetadataReadResult Failed(string failure)
        {
            return new MissionStartMetadataReadResult(
                false,
                Array.Empty<string>(),
                false,
                failure);
        }
    }

    private sealed class EligibilityReader
    {
        private readonly RuntimeShape _shape;
        private readonly RuntimeMappedGuestCatalogSnapshot _mappedGuestSnapshot;
        private readonly Dictionary<string, RuntimeScheduledEventEligibilityDiagnostic> _cache =
            new(StringComparer.Ordinal);

        private object? _allNpcs;
        private object? _recordedSpecialNpcs;

        public EligibilityReader(
            RuntimeShape shape,
            RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot)
        {
            _shape = shape;
            _mappedGuestSnapshot = mappedGuestSnapshot;
        }

        public RuntimeScheduledEventEligibilityDiagnostic Read(
            RuntimeScheduledEventTriggerDiagnostic trigger,
            bool eventFinished)
        {
            if (eventFinished
                || trigger.TriggerType
                    != RuntimeScheduledEventEligibility.KizunaCheckPointTrigger
                || string.IsNullOrEmpty(trigger.TriggerId))
            {
                return RuntimeScheduledEventEligibility.Evaluate(
                    trigger.TriggerType,
                    trigger.TriggerId,
                    eventFinished,
                    kizunaEvidence: null);
            }

            if (_cache.TryGetValue(trigger.TriggerId, out var cached))
            {
                return cached;
            }

            var result = RuntimeScheduledEventEligibility.Evaluate(
                trigger.TriggerType,
                trigger.TriggerId,
                eventFinished: false,
                kizunaEvidence: ReadKizunaEvidence(trigger.TriggerId));
            _cache.Add(trigger.TriggerId, result);
            return result;
        }

        private RuntimeScheduledEventKizunaEvidence ReadKizunaEvidence(
            string triggerId)
        {
            EnsureAllNpcs();
            var npc = ReadRequiredDictionaryValue(
                _allNpcs!,
                triggerId,
                "DataBaseDay.allNPCs");
            if (npc.GetType() != _shape.NpcType)
            {
                throw new InvalidOperationException(
                    "kizuna-trigger-npc-runtime-type-mismatch");
            }
            if (_shape.NpcKey.GetValue(npc) is not string npcKey
                || !string.Equals(npcKey, triggerId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "kizuna-trigger-npc-key-mismatch");
            }

            var boxedIdentity = _shape.NpcIdentity.GetValue(npc);
            if (boxedIdentity == null
                || boxedIdentity.GetType() != _shape.SchedulerCharacterType)
            {
                throw new InvalidOperationException(
                    "kizuna-trigger-character-identity-type-mismatch");
            }
            if (_shape.CharacterId.GetValue(boxedIdentity) is not int characterId
                || characterId < 0)
            {
                throw new InvalidOperationException(
                    "kizuna-trigger-character-id-invalid");
            }
            var isSpecial = !RuntimeSchedulerCharacterIdentity.IsNormal(boxedIdentity);
            if (!isSpecial)
            {
                return new RuntimeScheduledEventKizunaEvidence(
                    CharacterIdentityResolved: true,
                    RuntimeGuestId: null,
                    CanonicalCharacterId: characterId,
                    CharacterIsSpecial: false,
                    RecordedSpecialNpc: null,
                    CurrentBondLevel: null,
                    CurrentBondExp: null,
                    Level5Gate: null);
            }

            var matchingIdentities = _mappedGuestSnapshot.Entries
                .Where(entry => string.Equals(
                    entry.RuntimeStringId,
                    triggerId,
                    StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matchingIdentities.Length != 1)
            {
                throw new InvalidOperationException(
                    $"kizuna-trigger-runtime-identity-count:{matchingIdentities.Length}");
            }

            var mappedIdentity = matchingIdentities[0];
            if (mappedIdentity.RuntimeId is not >= 0
                || mappedIdentity.SourceGuestId is not >= 0)
            {
                throw new InvalidOperationException(
                    "kizuna-trigger-runtime-identity-incomplete");
            }
            if (characterId != mappedIdentity.SourceGuestId.Value)
            {
                throw new InvalidOperationException(
                    "kizuna-trigger-character-id-canonical-mismatch");
            }

            EnsureRecordedSpecialNpcs();
            if (!TryReadDictionaryValue(
                    _recordedSpecialNpcs!,
                    characterId,
                    "RunTimeAlbum.RecordedSpecialNPCs",
                    out var kizunaData))
            {
                return new RuntimeScheduledEventKizunaEvidence(
                    CharacterIdentityResolved: true,
                    RuntimeGuestId: mappedIdentity.RuntimeId,
                    CanonicalCharacterId: characterId,
                    CharacterIsSpecial: true,
                    RecordedSpecialNpc: false,
                    CurrentBondLevel: null,
                    CurrentBondExp: null,
                    Level5Gate: null);
            }
            if (kizunaData == null
                || kizunaData.GetType() != _shape.SpecialGuestRunTimeDataType)
            {
                throw new InvalidOperationException(
                    "recorded-kizuna-runtime-type-mismatch");
            }

            var currentBondLevel = ReadExactInt32(
                _shape.CurrentBondLevel,
                kizunaData,
                "CurrentBondLevel");
            var currentBondExp = ReadExactInt32(
                _shape.CurrentBondExp,
                kizunaData,
                "CurrentBondExp");
            bool? level5Gate = currentBondLevel == 4
                ? ReadExactBoolean(
                    _shape.ShouldHaveLevel5KizunaEvent,
                    instance: null,
                    source: "ShouldHaveLevel5KizunaEvent")
                : null;
            return new RuntimeScheduledEventKizunaEvidence(
                CharacterIdentityResolved: true,
                RuntimeGuestId: mappedIdentity.RuntimeId,
                CanonicalCharacterId: characterId,
                CharacterIsSpecial: true,
                RecordedSpecialNpc: true,
                CurrentBondLevel: currentBondLevel,
                CurrentBondExp: currentBondExp,
                Level5Gate: level5Gate);
        }

        private void EnsureAllNpcs()
        {
            if (_allNpcs != null) return;
            _allNpcs = ReadRequiredStaticValue(
                _shape.AllNpcs,
                "DataBaseDay.allNPCs");
        }

        private void EnsureRecordedSpecialNpcs()
        {
            if (_recordedSpecialNpcs != null) return;
            _recordedSpecialNpcs = ReadRequiredStaticValue(
                _shape.RecordedSpecialNpcs,
                "RunTimeAlbum.RecordedSpecialNPCs");
        }

        private static object ReadRequiredDictionaryValue(
            object dictionary,
            object key,
            string source)
        {
            if (!TryReadDictionaryValue(
                    dictionary,
                    key,
                    source,
                    out var value))
            {
                throw new InvalidOperationException(
                    $"{source}-key-missing");
            }
            return value
                ?? throw new InvalidOperationException(
                    $"{source}-value-null");
        }

        private static bool TryReadDictionaryValue(
            object dictionary,
            object key,
            string source,
            out object? value)
        {
            if (!RuntimeConcreteCollectionReader.TryGetDictionaryValue(
                    dictionary,
                    key,
                    out value,
                    out var found,
                    out var failure))
            {
                throw new InvalidOperationException(
                    $"{source}-keyed-read-{FormatFailure(failure)}");
            }
            return found;
        }

        private static object ReadRequiredStaticValue(
            PropertyInfo property,
            string source)
        {
            var value = property.GetValue(null)
                ?? throw new InvalidOperationException($"{source}-missing");
            if (value.GetType() != property.PropertyType)
            {
                throw new InvalidOperationException(
                    $"{source}-runtime-type-mismatch");
            }
            return value;
        }

        private static int ReadExactInt32(
            PropertyInfo property,
            object instance,
            string source)
        {
            return property.GetValue(instance) is int value
                ? value
                : throw new InvalidOperationException(
                    $"{source}-value-type-mismatch");
        }

        private static bool ReadExactBoolean(
            PropertyInfo property,
            object? instance,
            string source)
        {
            return property.GetValue(instance) is bool value
                ? value
                : throw new InvalidOperationException(
                    $"{source}-value-type-mismatch");
        }
    }

    private sealed record RuntimeShape(
        Type GameDateType,
        Type NpcType,
        Type SchedulerCharacterType,
        Type SpecialGuestRunTimeDataType,
        Type EventNodeType,
        Type MissionNodeType,
        Type ScheduledEventType,
        Type TriggerType,
        Type TriggerKindType,
        Type DayType,
        Type DayKindType,
        Type DayCalculateType,
        MethodInfo GetDay,
        PropertyInfo CorrectedDay,
        PropertyInfo ScheduledEvents,
        PropertyInfo FinishedEvents,
        PropertyInfo FinishedMissions,
        PropertyInfo AllNpcs,
        PropertyInfo RecordedSpecialNpcs,
        PropertyInfo ShouldHaveLevel5KizunaEvent,
        PropertyInfo NpcKey,
        PropertyInfo NpcIdentity,
        FieldInfo CharacterId,
        PropertyInfo CurrentBondLevel,
        PropertyInfo CurrentBondExp,
        MethodInfo TargetNodeExists,
        MethodInfo RefEvent,
        MethodInfo RefMission,
        PropertyInfo Label,
        PropertyInfo PreNodes,
        PropertyInfo LoopedMission,
        PropertyInfo ScheduledEvent,
        PropertyInfo Trigger,
        PropertyInfo TriggerKind,
        PropertyInfo TriggerId,
        PropertyInfo TriggerTime,
        FieldInfo TimeDayKind,
        FieldInfo TimeCalculateKind,
        FieldInfo TimeDay,
        FieldInfo TimeDayRange,
        PropertyInfo PostMissions,
        PropertyInfo PostMissionsAfterPerformance)
    {
        public static RuntimeShape Resolve()
        {
            var runtimeSchedulerType = RequireType(RunTimeSchedulerTypeName);
            var runtimePlayerDataType = RequireType(RunTimePlayerDataTypeName);
            var runtimeAlbumType = RequireType(RunTimeAlbumTypeName);
            var specialGuestRunTimeDataType =
                RequireType(SpecialGuestRunTimeDataTypeName);
            var gameDateType = RequireType(GameDateTypeName);
            var databaseSchedulerType = RequireType(DataBaseSchedulerTypeName);
            var databaseDayType = RequireType(DataBaseDayTypeName);
            var npcType = RequireType(NpcTypeName);
            var schedulerNodeType = RequireType(SchedulerNodeTypeName);
            var schedulerCharacterType = RequireType(SchedulerCharacterTypeName);
            var eventNodeType = RequireType(EventNodeTypeName);
            var missionNodeType = RequireType(MissionNodeTypeName);
            var scheduledEventType = RequireType(ScheduledEventTypeName);
            var triggerType = RequireType(TriggerTypeName);
            var triggerKindType = RequireType(TriggerKindTypeName);
            var dayType = RequireType(DayTypeName);
            var dayKindType = RequireType(DayKindTypeName);
            var dayCalculateType = RequireType(DayCalculateTypeName);
            ValidateInt32Enum(triggerKindType, TriggerKindTypeName);
            ValidateEnumValue(
                triggerKindType,
                "OnTalkWithCharacter",
                RuntimeScheduledEventEligibility.OnTalkWithCharacterTrigger);
            ValidateEnumValue(
                triggerKindType,
                "KizunaCheckPoint",
                RuntimeScheduledEventEligibility.KizunaCheckPointTrigger);
            ValidateInt32Enum(dayKindType, DayKindTypeName);
            ValidateInt32Enum(dayCalculateType, DayCalculateTypeName);

            var stringListType = RequireClosedGenericType(
                Il2CppListTypeName,
                typeof(string));
            var scheduledEvents = RequireExactStaticProperty(
                runtimeSchedulerType,
                "scheduledEvents",
                propertyType => IsExactClosedGeneric(
                    propertyType,
                    Il2CppDictionaryTypeName,
                    typeof(int),
                    stringListType));
            var allNpcs = RequireExactStaticProperty(
                databaseDayType,
                "allNPCs",
                propertyType => IsExactClosedGeneric(
                    propertyType,
                    Il2CppDictionaryTypeName,
                    typeof(string),
                    npcType));
            var recordedSpecialNpcs = RequireExactStaticProperty(
                runtimeAlbumType,
                "RecordedSpecialNPCs",
                propertyType => IsExactClosedGeneric(
                    propertyType,
                    Il2CppDictionaryTypeName,
                    typeof(int),
                    specialGuestRunTimeDataType));
            return new RuntimeShape(
                gameDateType,
                npcType,
                schedulerCharacterType,
                specialGuestRunTimeDataType,
                eventNodeType,
                missionNodeType,
                scheduledEventType,
                triggerType,
                triggerKindType,
                dayType,
                dayKindType,
                dayCalculateType,
                RequireExactStaticMethod(
                    runtimePlayerDataType,
                    "GetDay",
                    gameDateType),
                RequireExactInstanceProperty(
                    gameDateType,
                    "CorrectedDay",
                    typeof(int)),
                scheduledEvents,
                RequireExactStaticProperty(
                    runtimeSchedulerType,
                    "finishedEvents",
                    stringListType),
                RequireExactStaticProperty(
                    runtimeSchedulerType,
                    "finishedMissions",
                    stringListType),
                allNpcs,
                recordedSpecialNpcs,
                RequireExactStaticProperty(
                    runtimePlayerDataType,
                    "ShouldHaveLevel5KizunaEvent",
                    typeof(bool)),
                RequireExactInstanceProperty(
                    npcType,
                    "key",
                    typeof(string)),
                RequireExactInstanceProperty(
                    npcType,
                    "identity",
                    schedulerCharacterType),
                RequireExactInstanceField(
                    schedulerCharacterType,
                    "characterId",
                    typeof(int)),
                RequireExactInstanceProperty(
                    specialGuestRunTimeDataType,
                    "CurrentBondLevel",
                    typeof(int)),
                RequireExactInstanceProperty(
                    specialGuestRunTimeDataType,
                    "CurrentBondExp",
                    typeof(int)),
                RequireExactStaticMethod(
                    databaseSchedulerType,
                    "TargetNodeExists",
                    typeof(bool),
                    typeof(string)),
                RequireExactStaticMethod(
                    databaseSchedulerType,
                    "RefEvent",
                    eventNodeType,
                    typeof(string)),
                RequireExactStaticMethod(
                    databaseSchedulerType,
                    "RefMission",
                    missionNodeType,
                    typeof(string)),
                RequireExactInstanceProperty(
                    schedulerNodeType,
                    "label",
                    typeof(string)),
                RequireExactInstanceProperty(
                    schedulerNodeType,
                    "preNodes",
                    propertyType => string.Equals(
                        propertyType.FullName,
                        Il2CppStringArrayTypeName,
                        StringComparison.Ordinal)),
                RequireExactInstanceProperty(
                    missionNodeType,
                    "loopedMission",
                    typeof(bool)),
                RequireExactInstanceProperty(
                    eventNodeType,
                    "scheduledEvent",
                    scheduledEventType),
                RequireExactInstanceProperty(
                    scheduledEventType,
                    "trigger",
                    triggerType),
                RequireExactInstanceProperty(
                    triggerType,
                    "triggerType",
                    triggerKindType),
                RequireExactInstanceProperty(
                    triggerType,
                    "triggerId",
                    typeof(string)),
                RequireExactInstanceProperty(
                    triggerType,
                    "time",
                    dayType),
                RequireExactInstanceField(
                    dayType,
                    "dayType",
                    dayKindType),
                RequireExactInstanceField(
                    dayType,
                    "dayCalcType",
                    dayCalculateType),
                RequireExactInstanceField(
                    dayType,
                    "day",
                    typeof(int)),
                RequireExactInstanceField(
                    dayType,
                    "dayRange",
                    typeof(Vector2Int)),
                RequireExactInstanceProperty(
                    schedulerNodeType,
                    "postMissions",
                    propertyType => string.Equals(
                        propertyType.FullName,
                        Il2CppStringArrayTypeName,
                        StringComparison.Ordinal)),
                RequireExactInstanceProperty(
                    schedulerNodeType,
                    "postMissionsAfterPerformance",
                    propertyType => string.Equals(
                        propertyType.FullName,
                        Il2CppStringArrayTypeName,
                        StringComparison.Ordinal)));
        }

        private static Type RequireType(string fullName)
        {
            return RuntimeReflectionUtility.FindType(fullName)
                ?? throw new InvalidOperationException($"{fullName} is not loaded.");
        }

        private static Type RequireClosedGenericType(
            string definitionName,
            params Type[] arguments)
        {
            var definition = RuntimeReflectionUtility.FindType(definitionName)
                ?? throw new InvalidOperationException($"{definitionName} is not loaded.");
            if (!definition.IsGenericTypeDefinition)
            {
                throw new InvalidOperationException(
                    $"{definitionName} is not a generic type definition.");
            }
            return definition.MakeGenericType(arguments);
        }

        private static MethodInfo RequireExactStaticMethod(
            Type declaringType,
            string methodName,
            Type returnType,
            params Type[] parameterTypes)
        {
            var matches = declaringType
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, methodName, StringComparison.Ordinal)
                    && !method.IsGenericMethod
                    && method.ReturnType == returnType
                    && method.GetParameters().Select(parameter => parameter.ParameterType)
                        .SequenceEqual(parameterTypes))
                .Take(2)
                .ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new MissingMethodException(declaringType.FullName, methodName);
        }

        private static PropertyInfo RequireExactStaticProperty(
            Type declaringType,
            string propertyName,
            Type propertyType)
        {
            return RequireExactProperty(
                declaringType,
                propertyName,
                isStatic: true,
                candidate => candidate == propertyType);
        }

        private static PropertyInfo RequireExactStaticProperty(
            Type declaringType,
            string propertyName,
            Func<Type, bool> propertyType)
        {
            return RequireExactProperty(
                declaringType,
                propertyName,
                isStatic: true,
                propertyType);
        }

        private static PropertyInfo RequireExactInstanceProperty(
            Type declaringType,
            string propertyName,
            Type propertyType)
        {
            return RequireExactProperty(
                declaringType,
                propertyName,
                isStatic: false,
                candidate => candidate == propertyType);
        }

        private static PropertyInfo RequireExactInstanceProperty(
            Type declaringType,
            string propertyName,
            Func<Type, bool> propertyType)
        {
            return RequireExactProperty(
                declaringType,
                propertyName,
                isStatic: false,
                propertyType);
        }

        private static FieldInfo RequireExactInstanceField(
            Type declaringType,
            string fieldName,
            Type fieldType)
        {
            var matches = declaringType
                .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(field =>
                    string.Equals(field.Name, fieldName, StringComparison.Ordinal)
                    && !field.IsStatic
                    && field.FieldType == fieldType)
                .Take(2)
                .ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new MissingFieldException(declaringType.FullName, fieldName);
        }

        private static PropertyInfo RequireExactProperty(
            Type declaringType,
            string propertyName,
            bool isStatic,
            Func<Type, bool> propertyType)
        {
            var flags = BindingFlags.Public
                | BindingFlags.DeclaredOnly
                | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var matches = declaringType.GetProperties(flags)
                .Where(property =>
                {
                    if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal)
                        || property.GetIndexParameters().Length != 0
                        || !propertyType(property.PropertyType))
                    {
                        return false;
                    }

                    var getter = property.GetGetMethod(nonPublic: false);
                    return getter != null && getter.IsStatic == isStatic;
                })
                .Take(2)
                .ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new MissingMemberException(declaringType.FullName, propertyName);
        }

        private static bool IsExactClosedGeneric(
            Type candidate,
            string definitionName,
            params Type[] arguments)
        {
            return candidate.IsGenericType
                && string.Equals(
                    candidate.GetGenericTypeDefinition().FullName,
                    definitionName,
                    StringComparison.Ordinal)
                && candidate.GetGenericArguments().SequenceEqual(arguments);
        }

        private static void ValidateInt32Enum(Type type, string typeName)
        {
            if (!type.IsEnum || Enum.GetUnderlyingType(type) != typeof(int))
            {
                throw new InvalidOperationException($"{typeName} is not an Int32 enum.");
            }
        }

        private static void ValidateEnumValue(
            Type type,
            string memberName,
            int expectedValue)
        {
            var value = Enum.Parse(type, memberName, ignoreCase: false);
            if (Convert.ToInt32(value, CultureInfo.InvariantCulture) != expectedValue)
            {
                throw new InvalidOperationException(
                    $"{type.FullName}.{memberName} value changed.");
            }
        }
    }
}
