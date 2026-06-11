using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

public static class RuntimeMissionSnapshotService
{
    private const string DataBaseDayTypeName = "GameData.Core.Collections.DaySceneUtility.DataBaseDay";
    private const string RunTimeSchedulerTypeName = "GameData.RunTime.Common.RunTimeScheduler";
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string RunTimeDaySceneTypeName = "GameData.RunTime.DaySceneUtility.RunTimeDayScene";
    private const string DaySceneMapTypeName = "DayScene.DaySceneMap";
    private const string CharacterConditionComponentTypeName = "DayScene.Interactables.Collections.ConditionComponents.CharacterConditionComponent";
    private const string MissionInteractConditionComponentTypeName = "DayScene.Interactables.Collections.ConditionComponents.MissionInteractConditionComponent";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(3);
    private static RuntimeMissionContext? _cachedContext;
    private static DateTime _cachedAtUtc;

    public static RuntimeMissionContext Load()
    {
        var now = DateTime.UtcNow;
        if (_cachedContext != null && now - _cachedAtUtc < CacheDuration) return _cachedContext;

        _cachedContext = LoadUncached();
        _cachedAtUtc = now;
        return _cachedContext;
    }

    private static RuntimeMissionContext LoadUncached()
    {
        var missions = new List<RuntimeMissionInfo>();
        var errors = new List<string>();
        var source = new List<string>();

        var dataBaseDayType = RuntimeReflectionUtility.FindType(DataBaseDayTypeName);
        var schedulerType = RuntimeReflectionUtility.FindType(RunTimeSchedulerTypeName);
        if (dataBaseDayType == null || schedulerType == null)
        {
            return new RuntimeMissionContext
            {
                Source = $"DataBaseDay={(dataBaseDayType == null ? "missing" : "ok")}; Scheduler={(schedulerType == null ? "missing" : "ok")}",
                Error = "任务运行时类型未加载，可能尚未读取存档。",
            };
        }

        var runtimeDaySceneType = RuntimeReflectionUtility.FindType(RunTimeDaySceneTypeName);
        var daySceneMapType = RuntimeReflectionUtility.FindType(DaySceneMapTypeName);
        var characterComponentType = RuntimeReflectionUtility.FindType(CharacterConditionComponentTypeName);
        var missionInteractType = RuntimeReflectionUtility.FindType(MissionInteractConditionComponentTypeName);
        source.Add($"types=RuntimeDayScene={(runtimeDaySceneType == null ? "missing" : "ok")}; DaySceneMap={(daySceneMapType == null ? "missing" : "ok")}; CharacterCondition={(characterComponentType == null ? "missing" : "ok")}; MissionInteract={(missionInteractType == null ? "missing" : "ok")}");

        var trackingMissions = ReadTrackingMissions(schedulerType, errors).ToList();
        source.Add($"trackingMissions={trackingMissions.Count}");

        var npcKeys = ReadGlobalNpcKeys(dataBaseDayType, errors).ToList();
        source.Add($"npcKeys={npcKeys.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, npcKeys, "InteractMission");

        var trackedNpcLabels = ReadTrackedNpcLabels(runtimeDaySceneType, errors).Except(npcKeys, StringComparer.Ordinal).ToList();
        source.Add($"trackedNpcLabels={trackedNpcLabels.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, trackedNpcLabels, "TrackedNPC");

        var daySceneMapLabels = ReadDaySceneMapCharacterLabels(daySceneMapType, errors)
            .Except(npcKeys.Concat(trackedNpcLabels), StringComparer.Ordinal)
            .ToList();
        source.Add($"daySceneMapCharacters={daySceneMapLabels.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, daySceneMapLabels, "DaySceneMap");

        var sceneCharacterLabels = ReadSceneCharacterLabels(characterComponentType, errors)
            .Except(npcKeys.Concat(trackedNpcLabels).Concat(daySceneMapLabels), StringComparer.Ordinal)
            .ToList();
        source.Add($"sceneCharacters={sceneCharacterLabels.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, sceneCharacterLabels, "SceneCharacter");

        var trackedInteractableLabels = ReadTrackedInteractableLabels(runtimeDaySceneType, errors).ToList();
        source.Add($"trackedInteractables={trackedInteractableLabels.Count}");
        var trackedInteractableMissions = ReadInteractableMissions(schedulerType, trackingMissions, trackedInteractableLabels, "TrackedInteractable", errors).ToList();
        source.Add($"trackedInteractableMissions={trackedInteractableMissions.Count}");
        missions.AddRange(trackedInteractableMissions);

        var sceneInteractableLabels = ReadSceneInteractableLabels(missionInteractType, errors).Except(trackedInteractableLabels, StringComparer.Ordinal).ToList();
        source.Add($"sceneInteractableKeys={sceneInteractableLabels.Count}");
        var sceneInteractableMissions = ReadInteractableMissions(schedulerType, trackingMissions, sceneInteractableLabels, "SceneInteractable", errors).ToList();
        source.Add($"sceneInteractables={sceneInteractableMissions.Count}");
        missions.AddRange(sceneInteractableMissions);

        var fallbackMissions = ReadTrackingMissionFallbackMissions(dataBaseDayType, schedulerType, trackingMissions).ToList();
        source.Add($"trackingFallback={fallbackMissions.Count}");
        missions.AddRange(fallbackMissions);

        var deduplicated = missions
            .GroupBy(mission => $"{mission.Label}|{mission.CharacterLabel}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(mission => mission.CharacterName, StringComparer.Ordinal)
            .ThenBy(mission => mission.Title, StringComparer.Ordinal)
            .ToList();
        source.Add($"available={deduplicated.Count}");

        return new RuntimeMissionContext
        {
            AvailableMissions = deduplicated,
            Source = string.Join("; ", source),
            Error = errors.Count == 0 ? null : string.Join("; ", errors),
        };
    }

    private static IEnumerable<string> ReadGlobalNpcKeys(Type dataBaseDayType, List<string> errors)
    {
        List<string> result;
        try
        {
            result = RuntimeReflectionUtility
                .EnumerateObjects(RuntimeReflectionUtility.InvokeStaticMethod(dataBaseDayType, "GetAllNPCKeys"))
                .Select(value => value?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            errors.Add($"npcKeys: {ex.Message}");
            result = new List<string>();
        }

        return result;
    }

    private static void AddAvailableInteractMissions(
        List<RuntimeMissionInfo> missions,
        List<string> errors,
        Type dataBaseDayType,
        Type schedulerType,
        IEnumerable<string> characterLabels,
        string source)
    {
        foreach (var characterLabel in characterLabels)
        {
            try
            {
                var availableLabels = RuntimeReflectionUtility
                    .EnumerateObjects(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "GetAvailableInteractMissionForCharacter", characterLabel))
                    .Select(value => value?.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                foreach (var label in availableLabels)
                {
                    var finished = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "HaveMissionFinished", label));
                    if (finished) continue;

                    missions.Add(new RuntimeMissionInfo
                    {
                        Label = label,
                        Title = ResolveMissionTitle(label),
                        CharacterLabel = characterLabel,
                        CharacterName = ResolveNpcName(dataBaseDayType, characterLabel),
                        Source = source,
                        Started = false,
                        Finished = finished,
                    });
                }
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"{characterLabel}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<object?> ReadTrackingMissions(Type schedulerType, List<string> errors)
    {
        object? trackingMissionMaps;
        try
        {
            trackingMissionMaps = RuntimeReflectionUtility.GetStaticMemberValue(schedulerType, "trackingMissions");
        }
        catch (Exception ex)
        {
            errors.Add($"tracking missions: {ex.Message}");
            yield break;
        }

        if (trackingMissionMaps == null) yield break;

        foreach (var bucketCandidate in RuntimeReflectionUtility.EnumerateObjects(trackingMissionMaps))
        {
            var bucket = RuntimeReflectionUtility.NormalizeKeyValueValue(bucketCandidate);
            foreach (var missionCandidate in RuntimeReflectionUtility.EnumerateObjects(bucket))
            {
                var mission = RuntimeReflectionUtility.NormalizeKeyValueValue(missionCandidate);
                if (mission != null) yield return mission;
            }
        }
    }

    private static IEnumerable<string> ReadTrackedNpcLabels(Type? runtimeDaySceneType, List<string> errors)
    {
        if (runtimeDaySceneType == null) yield break;

        var trackedNpcMaps = RuntimeReflectionUtility.GetStaticMemberValue(runtimeDaySceneType, "trackedNPCs");
        if (trackedNpcMaps == null) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapCandidate in RuntimeReflectionUtility.EnumerateObjects(trackedNpcMaps))
        {
            var map = RuntimeReflectionUtility.NormalizeKeyValueValue(mapCandidate);
            foreach (var npcCandidate in RuntimeReflectionUtility.EnumerateObjects(map))
            {
                var npc = RuntimeReflectionUtility.NormalizeKeyValueValue(npcCandidate);
                var label = ReadTextMember(npc, "key")
                    ?? ReadTextMember(npcCandidate, "Key")
                    ?? ReadTextMember(npcCandidate, "key");
                if (string.IsNullOrWhiteSpace(label) || !seen.Add(label)) continue;
                yield return label;
            }
        }
    }

    private static IEnumerable<string> ReadDaySceneMapCharacterLabels(Type? daySceneMapType, List<string> errors)
    {
        if (daySceneMapType == null) yield break;

        var allCharacters = RuntimeReflectionUtility.GetStaticMemberValue(daySceneMapType, "allCharacters");
        if (allCharacters == null) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var characterCandidate in RuntimeReflectionUtility.EnumerateObjects(allCharacters))
        {
            var component = RuntimeReflectionUtility.NormalizeKeyValueValue(characterCandidate);
            string? label = null;
            try
            {
                label = ReadTextMember(characterCandidate, "Key")
                    ?? ReadTextMember(component, "CharacterLabel");
                var trackedNpcData = RuntimeReflectionUtility.GetMemberValue(component, "trackedNPCData");
                label ??= ReadTextMember(trackedNpcData, "key");
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"day scene map character: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(label) || !seen.Add(label)) continue;
            yield return label;
        }
    }

    private static IEnumerable<string> ReadSceneCharacterLabels(Type? characterComponentType, List<string> errors)
    {
        if (characterComponentType == null) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in RuntimeReflectionUtility.FindUnityObjectsIncludingInactive(characterComponentType))
        {
            string? label = null;
            try
            {
                label = ReadTextMember(component, "CharacterLabel");
                var trackedNpcData = RuntimeReflectionUtility.GetMemberValue(component, "trackedNPCData");
                label ??= ReadTextMember(trackedNpcData, "key");
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"scene character: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(label) || !seen.Add(label)) continue;
            yield return label;
        }
    }

    private static IEnumerable<string> ReadTrackedInteractableLabels(Type? runtimeDaySceneType, List<string> errors)
    {
        if (runtimeDaySceneType == null) yield break;

        var trackedInteractables = RuntimeReflectionUtility.GetStaticMemberValue(runtimeDaySceneType, "trackedInteradctables");
        if (trackedInteractables == null) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var interactableCandidate in RuntimeReflectionUtility.EnumerateObjects(trackedInteractables))
        {
            var interactable = RuntimeReflectionUtility.NormalizeKeyValueValue(interactableCandidate);
            string? label = null;
            try
            {
                var openStatusValue = RuntimeReflectionUtility.GetMemberValue(interactable, "openStatus");
                if (openStatusValue != null && !RuntimeReflectionUtility.ToBool(openStatusValue)) continue;

                label = ReadTextMember(interactableCandidate, "Key")
                    ?? ReadTextMember(interactable, "label");
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"tracked interactable: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(label) || !seen.Add(label)) continue;
            yield return label;
        }
    }

    private static IEnumerable<string> ReadSceneInteractableLabels(Type? missionInteractType, List<string> errors)
    {
        if (missionInteractType == null) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in RuntimeReflectionUtility.FindUnityObjectsIncludingInactive(missionInteractType))
        {
            string? interactableKey = null;
            var available = true;
            try
            {
                var availableValue = RuntimeReflectionUtility.GetMemberValue(component, "CheckAvailability");
                if (availableValue != null) available = RuntimeReflectionUtility.ToBool(availableValue);
                interactableKey = ReadTextMember(component, "interactableKey");
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"scene interactable: {ex.Message}");
            }

            if (!available || string.IsNullOrWhiteSpace(interactableKey) || !seen.Add(interactableKey)) continue;
            yield return interactableKey;
        }
    }

    private static IEnumerable<RuntimeMissionInfo> ReadInteractableMissions(
        Type schedulerType,
        IReadOnlyList<object?> trackingMissions,
        IEnumerable<string> interactableLabels,
        string source,
        List<string> errors)
    {
        var interactables = interactableLabels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (interactables.Count == 0) yield break;

        var matchedInteractables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trackedMission in trackingMissions)
        {
            var missionLabel = ReadTextMember(trackedMission, "missionLabel");
            if (string.IsNullOrWhiteSpace(missionLabel)) continue;

            var finished = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "HaveMissionFinished", missionLabel));
            if (finished) continue;

            foreach (var snapshot in EnumerateConditionSnapshots(trackedMission))
            {
                string? interactableLabel = null;
                try
                {
                    if (!IsConditionType(snapshot.Condition, "InspectInteractable", 2) || snapshot.Finished) continue;
                    interactableLabel = ReadTextMember(snapshot.Condition, "label");
                    if (string.IsNullOrWhiteSpace(interactableLabel) || !interactables.Contains(interactableLabel)) continue;
                }
                catch (Exception ex)
                {
                    if (errors.Count < 8) errors.Add($"interactable mission: {ex.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(interactableLabel)) continue;
                matchedInteractables.Add(interactableLabel);
                yield return new RuntimeMissionInfo
                {
                    Label = missionLabel,
                    Title = ResolveMissionTitle(missionLabel),
                    CharacterLabel = interactableLabel,
                    CharacterName = "场景交互",
                    Source = source,
                    Started = false,
                    Finished = finished,
                };
            }
        }

        foreach (var interactableLabel in interactables.Except(matchedInteractables, StringComparer.Ordinal))
        {
            yield return new RuntimeMissionInfo
            {
                Label = interactableLabel,
                Title = ResolveMissionTitle(interactableLabel),
                CharacterLabel = interactableLabel,
                CharacterName = "场景交互",
                Source = $"{source}Key",
                Started = false,
                Finished = false,
            };
        }
    }

    private static IEnumerable<RuntimeMissionInfo> ReadTrackingMissionFallbackMissions(
        Type dataBaseDayType,
        Type schedulerType,
        IReadOnlyList<object?> trackingMissions)
    {
        foreach (var trackedMission in trackingMissions)
        {
            var missionLabel = ReadTextMember(trackedMission, "missionLabel");
            if (string.IsNullOrWhiteSpace(missionLabel)) continue;

            var finished = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "HaveMissionFinished", missionLabel));
            if (finished) continue;

            var conditions = EnumerateConditionSnapshots(trackedMission).ToList();
            var firstUnfinished = conditions.FirstOrDefault(condition => !condition.Finished);
            var talkCondition = conditions.FirstOrDefault(condition => IsConditionType(condition.Condition, "TalkWithCharacter", 1));

            if (firstUnfinished != null && IsConditionType(firstUnfinished.Condition, "TalkWithCharacter", 1))
            {
                var characterLabel = ReadTextMember(firstUnfinished.Condition, "label");
                if (!string.IsNullOrWhiteSpace(characterLabel))
                {
                    yield return new RuntimeMissionInfo
                    {
                        Label = missionLabel,
                        Title = ResolveMissionTitle(missionLabel),
                        CharacterLabel = characterLabel,
                        CharacterName = ResolveNpcName(dataBaseDayType, characterLabel),
                        Source = "TrackingMission:PendingTalk",
                        Started = false,
                        Finished = finished,
                    };
                    continue;
                }
            }

            if (firstUnfinished != null && IsConditionType(firstUnfinished.Condition, "InspectInteractable", 2))
            {
                var interactableLabel = ReadTextMember(firstUnfinished.Condition, "label");
                if (!string.IsNullOrWhiteSpace(interactableLabel))
                {
                    yield return new RuntimeMissionInfo
                    {
                        Label = missionLabel,
                        Title = ResolveMissionTitle(missionLabel),
                        CharacterLabel = interactableLabel,
                        CharacterName = "场景交互",
                        Source = "TrackingMission:PendingInspect",
                        Started = false,
                        Finished = finished,
                    };
                    continue;
                }
            }

            var activeCharacterLabel = ReadTextMember(firstUnfinished?.Condition, "label")
                ?? ReadTextMember(talkCondition?.Condition, "label")
                ?? missionLabel;
            var activeCharacterName = firstUnfinished != null && IsConditionType(firstUnfinished.Condition, "InspectInteractable", 2)
                ? "场景交互"
                : string.Equals(activeCharacterLabel, missionLabel, StringComparison.Ordinal)
                    ? "任务"
                    : ResolveNpcName(dataBaseDayType, activeCharacterLabel);

            yield return new RuntimeMissionInfo
            {
                Label = missionLabel,
                Title = ResolveMissionTitle(missionLabel),
                CharacterLabel = activeCharacterLabel,
                CharacterName = activeCharacterName,
                Source = firstUnfinished == null ? "TrackingMission:Tracked" : "TrackingMission:Active",
                Started = firstUnfinished != null && !IsInitialAcceptCondition(firstUnfinished.Condition),
                Finished = finished,
            };
        }
    }

    private static IEnumerable<MissionConditionSnapshot> EnumerateConditionSnapshots(object? trackedMission)
    {
        if (trackedMission == null) yield break;

        var mission = RuntimeReflectionUtility.InvokeMethod(trackedMission, "GetMissionReference");
        var finishConditions = RuntimeReflectionUtility.GetMemberValue(mission, "finishCondition");
        var finishStates = RuntimeReflectionUtility
            .EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(trackedMission, "conditionFinishStates"))
            .Select(RuntimeReflectionUtility.ToBool)
            .ToList();

        var index = 0;
        foreach (var condition in RuntimeReflectionUtility.EnumerateObjects(finishConditions))
        {
            yield return new MissionConditionSnapshot
            {
                Condition = condition,
                Finished = index < finishStates.Count && finishStates[index],
            };
            index++;
        }
    }

    private static bool IsInitialAcceptCondition(object? condition)
    {
        return IsConditionType(condition, "TalkWithCharacter", 1)
            || IsConditionType(condition, "InspectInteractable", 2);
    }

    private static bool IsConditionType(object? condition, string expectedName, int expectedValue)
    {
        var conditionType = RuntimeReflectionUtility.GetMemberValue(condition, "conditionType");
        if (conditionType == null) return false;
        if (RuntimeReflectionUtility.ToInt(conditionType, int.MinValue) == expectedValue) return true;
        return string.Equals(conditionType.ToString(), expectedName, StringComparison.Ordinal);
    }

    private sealed class MissionConditionSnapshot
    {
        public object? Condition { get; init; }
        public bool Finished { get; init; }
    }

    private static string? ReadTextMember(object? value, string memberName)
    {
        var memberValue = RuntimeReflectionUtility.GetMemberValue(value, memberName);
        return memberValue?.ToString();
    }

    private static string ResolveMissionTitle(string label)
    {
        var languageType = RuntimeReflectionUtility.FindType(DataBaseLanguageTypeName);
        var language = languageType == null
            ? null
            : RuntimeReflectionUtility.InvokeStaticMethod(languageType, "GetMissionLanguage", label);
        var text = ReadTextLikeValue(language);
        return string.IsNullOrWhiteSpace(text) ? label : text;
    }

    private static string ResolveNpcName(Type dataBaseDayType, string npcKey)
    {
        var npc = RuntimeReflectionUtility.InvokeStaticMethod(dataBaseDayType, "RefNPC", npcKey);
        var text = ReadTextLikeValue(npc);
        return string.IsNullOrWhiteSpace(text) ? npcKey : text;
    }

    private static string ReadTextLikeValue(object? value)
    {
        if (value == null) return "";

        foreach (var member in new[] { "Name", "name", "Title", "title", "Text", "text", "Value", "value", "Description", "description", "Chinese", "Zh", "zh" })
        {
            try
            {
                var memberValue = RuntimeReflectionUtility.GetMemberValue(value, member)?.ToString();
                if (!string.IsNullOrWhiteSpace(memberValue)) return memberValue;
            }
            catch
            {
                // Try the next common text member.
            }
        }

        try
        {
            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith(value.GetType().FullName ?? value.GetType().Name, StringComparison.Ordinal))
            {
                return text;
            }
        }
        catch
        {
            // Ignore conversion failures.
        }

        return "";
    }
}
