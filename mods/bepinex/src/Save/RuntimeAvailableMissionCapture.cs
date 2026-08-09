namespace MystiaStewardCompanion.Save;

internal static class RuntimeAvailableMissionCapture
{
    public const string EligibleDisposition = "eligible";
    private const int MaxCandidateCount = 4096;
    private const int MaxFinishedLabelCount = 20_000;
    private const int MaxPreNodeCount = 4096;
    private const int MaxIdentityLength = 512;

    public static RuntimeAvailableMissionSnapshot Project(
        RuntimeAvailableMissionCaptureInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!input.Complete)
        {
            return RuntimeAvailableMissionSnapshot.Unavailable(
                input.MissionGeneration,
                input.SourceRevision,
                string.IsNullOrEmpty(input.Error)
                    ? "available-mission-source-incomplete"
                    : $"available-mission-source-incomplete:{input.Error}");
        }
        if (input.MissionGeneration <= 0
            || input.SourceRevision <= 0
            || input.SourceMissionChangeVersion < 0
            || input.FinishedEvents == null
            || input.FinishedMissions == null
            || input.Candidates == null
            || input.Error == null
            || input.Error.Length != 0
            || input.FinishedEvents.Count > MaxFinishedLabelCount
            || input.FinishedMissions.Count > MaxFinishedLabelCount
            || input.Candidates.Count > MaxCandidateCount)
        {
            return RuntimeAvailableMissionSnapshot.Unavailable(
                input.MissionGeneration,
                input.SourceRevision,
                "available-mission-source-invalid");
        }

        try
        {
            var finishedEvents = BuildIdentitySet(
                input.FinishedEvents,
                "finished-events");
            var finishedMissions = BuildIdentitySet(
                input.FinishedMissions,
                "finished-missions");
            var candidatesByMission = new Dictionary<
                string,
                List<RuntimeAvailableMissionCandidate>>(
                    StringComparer.Ordinal);

            foreach (var candidate in input.Candidates)
            {
                ArgumentNullException.ThrowIfNull(candidate);
                if (!RuntimeAvailableMissionTriggerClassifier.TryClassify(
                        candidate.TriggerType,
                        candidate.EligibilityDisposition,
                        candidate.ReferenceSource,
                        candidate.SourcePhase,
                        out _))
                {
                    continue;
                }

                ValidateCandidate(candidate);
                if (!candidatesByMission.TryGetValue(
                    candidate.MissionLabel,
                    out var sameMission))
                {
                    sameMission = new List<RuntimeAvailableMissionCandidate>();
                    candidatesByMission.Add(candidate.MissionLabel, sameMission);
                }
                else if (!HasCompatibleMissionMetadata(
                    sameMission[0],
                    candidate))
                {
                    throw new InvalidOperationException(
                        $"available-mission-metadata-conflict:{candidate.MissionLabel}");
                }

                sameMission.Add(candidate);
            }

            var available = new List<RuntimeAvailableMissionSnapshotEntry>(
                candidatesByMission.Count);
            foreach (var pair in candidatesByMission)
            {
                var representative = pair.Value[0];
                if (representative.Active
                    || (representative.Finished
                        && !representative.LoopedMission))
                {
                    continue;
                }

                var satisfied = pair.Value
                    .Where(candidate => PreconditionsSatisfied(
                        candidate,
                        finishedEvents,
                        finishedMissions))
                    .Select(candidate =>
                    {
                        if (!RuntimeAvailableMissionTriggerClassifier.TryClassify(
                                candidate.TriggerType,
                                candidate.EligibilityDisposition,
                                candidate.ReferenceSource,
                                candidate.SourcePhase,
                                out var classification))
                        {
                            throw new InvalidOperationException(
                                "supported-trigger-classification-changed");
                        }
                        return classification;
                    })
                    .ToArray();
                if (satisfied.Length == 0)
                {
                    continue;
                }
                var trigger = RuntimeAvailableMissionTriggerClassifier.Merge(
                    satisfied);

                available.Add(
                    new RuntimeAvailableMissionSnapshotEntry(
                        representative.MissionLabel,
                        representative.Title,
                        representative.ReceiverLabel,
                        representative.CharacterName,
                        representative.SceneNames.ToArray(),
                        representative.PresentationStatus,
                        trigger.ActivationMode,
                        trigger.ActivationStatus,
                        trigger.TriggerKind,
                        trigger.SourceTiming,
                        trigger.ActivationHint));
            }

            available.Sort(
                static (left, right) =>
                {
                    var labelOrder = string.CompareOrdinal(
                        left.Label,
                        right.Label);
                    return labelOrder != 0
                        ? labelOrder
                        : string.CompareOrdinal(left.Title, right.Title);
                });
            return new RuntimeAvailableMissionSnapshot(
                RuntimeAvailable: true,
                MissionGeneration: input.MissionGeneration,
                SourceRevision: input.SourceRevision,
                Status: RuntimeAvailableMissionSnapshot.ReadyStatus,
                Error: "",
                Missions: available.ToArray());
        }
        catch (Exception ex)
        {
            return RuntimeAvailableMissionSnapshot.Unavailable(
                input.MissionGeneration,
                input.SourceRevision,
                $"available-mission-projection-invalid:{ex.GetType().Name}:{ex.Message}");
        }
    }

    private static void ValidateCandidate(
        RuntimeAvailableMissionCandidate candidate)
    {
        RequireIdentity(candidate.SourceEventLabel, "source-event-label");
        RequireIdentity(candidate.MissionLabel, "mission-label");
        if (string.IsNullOrWhiteSpace(candidate.Title))
        {
            throw new InvalidOperationException("mission-title-missing");
        }
        if (!candidate.DefinitionAvailable)
        {
            throw new InvalidOperationException(
                $"mission-definition-unavailable:{candidate.MissionLabel}");
        }
        var noReceiver = string.Equals(
            candidate.PresentationStatus,
            RuntimeMissionPresentation.NoReceiverStatus,
            StringComparison.Ordinal);
        if (candidate.HasReceiver == noReceiver)
        {
            throw new InvalidOperationException(
                $"mission-presentation-receiver-shape-mismatch:{candidate.MissionLabel}");
        }
        if (!RuntimeMissionPresentation.IsValid(
                new RuntimeMissionPresentation(
                    candidate.ReceiverLabel,
                    candidate.CharacterName,
                    candidate.SceneNames,
                    candidate.PresentationStatus)))
        {
            throw new InvalidOperationException(
                $"mission-presentation-invalid:{candidate.MissionLabel}");
        }
        if (candidate.DefinitionConditionCount < 0
            || candidate.PreNodes == null
            || candidate.PreNodes.Count > MaxPreNodeCount)
        {
            throw new InvalidOperationException(
                $"mission-definition-invalid:{candidate.MissionLabel}");
        }

        foreach (var preNode in candidate.PreNodes)
        {
            RequireIdentity(preNode, "mission-pre-node");
        }
        if (candidate.PreNodes.Distinct(StringComparer.Ordinal).Count()
            != candidate.PreNodes.Count)
        {
            throw new InvalidOperationException(
                $"mission-pre-node-duplicate:{candidate.MissionLabel}");
        }
    }

    private static bool PreconditionsSatisfied(
        RuntimeAvailableMissionCandidate candidate,
        IReadOnlySet<string> finishedEvents,
        IReadOnlySet<string> finishedMissions)
    {
        foreach (var preNode in candidate.PreNodes)
        {
            if (string.Equals(
                    preNode,
                    candidate.SourceEventLabel,
                    StringComparison.Ordinal))
            {
                // FinishNodeExtern adds the source event to finishedEvents before
                // FinishSchedulerNodePost starts postMissionsAfterPerformance.
                continue;
            }
            if (!finishedEvents.Contains(preNode)
                && !finishedMissions.Contains(preNode))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasCompatibleMissionMetadata(
        RuntimeAvailableMissionCandidate left,
        RuntimeAvailableMissionCandidate right)
    {
        return string.Equals(left.MissionLabel, right.MissionLabel, StringComparison.Ordinal)
            && left.DefinitionAvailable == right.DefinitionAvailable
            && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
            && left.HasReceiver == right.HasReceiver
            && string.Equals(
                left.ReceiverLabel,
                right.ReceiverLabel,
                StringComparison.Ordinal)
            && string.Equals(
                left.CharacterName,
                right.CharacterName,
                StringComparison.Ordinal)
            && left.SceneNames.SequenceEqual(
                right.SceneNames,
                StringComparer.Ordinal)
            && string.Equals(
                left.PresentationStatus,
                right.PresentationStatus,
                StringComparison.Ordinal)
            && left.DefinitionConditionCount == right.DefinitionConditionCount
            && left.LoopedMission == right.LoopedMission
            && left.Active == right.Active
            && left.Finished == right.Finished
            && left.PreNodes.SequenceEqual(right.PreNodes, StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> BuildIdentitySet(
        IReadOnlyList<string> identities,
        string source)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            if (identity == null || identity.Length > MaxIdentityLength)
            {
                throw new InvalidOperationException($"{source}-identity-invalid");
            }
            result.Add(identity);
        }
        return result;
    }

    private static void RequireIdentity(string identity, string source)
    {
        if (string.IsNullOrEmpty(identity)
            || identity.Length > MaxIdentityLength)
        {
            throw new InvalidOperationException($"{source}-missing");
        }
    }
}
