using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

public static class RuntimeMissionSnapshotService
{
    private const string DataBaseDayTypeName = "GameData.Core.Collections.DaySceneUtility.DataBaseDay";
    private const string DataBaseSchedulerTypeName = "GameData.Core.Collections.DataBaseScheduler";
    private const string RunTimeSchedulerTypeName = "GameData.RunTime.Common.RunTimeScheduler";
    private const string RunTimePlayerDataTypeName = "GameData.RunTime.Common.RunTimePlayerData";
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string DaySceneLanguageTypeName = "GameData.CoreLanguage.Collections.DaySceneLanguage";
    private const string RunTimeDaySceneTypeName = "GameData.RunTime.DaySceneUtility.RunTimeDayScene";
    private const string DaySceneMapTypeName = "DayScene.DaySceneMap";
    private const string CharacterConditionComponentTypeName = "DayScene.Interactables.Collections.ConditionComponents.CharacterConditionComponent";
    private const string MissionInteractConditionComponentTypeName = "DayScene.Interactables.Collections.ConditionComponents.MissionInteractConditionComponent";
    private const string MissionStatusAvailable = "available";
    private const string MissionStatusTracking = "tracking";
    private const string MissionStatusFulfilled = "fulfilled";
    private const string MissionStatusFinished = "finished";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(3);
    private static RuntimeMissionContext? _cachedContext;
    private static DateTime _cachedAtUtc;

    public static void ClearCache()
    {
        _cachedContext = null;
        _cachedAtUtc = DateTime.MinValue;
    }

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
        var dataBaseSchedulerType = RuntimeReflectionUtility.FindType(DataBaseSchedulerTypeName);
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
        source.Add($"types=RuntimeDayScene={(runtimeDaySceneType == null ? "missing" : "ok")}; DaySceneMap={(daySceneMapType == null ? "missing" : "ok")}; CharacterCondition={(characterComponentType == null ? "missing" : "ok")}; MissionInteract={(missionInteractType == null ? "missing" : "ok")}; DataBaseScheduler={(dataBaseSchedulerType == null ? "missing" : "ok")}");

        var missionData = ReadTrackedMissionDataSnapshots(schedulerType, errors).ToList();
        var statusByMission = missionData
            .Where(mission => !string.IsNullOrWhiteSpace(mission.Label))
            .GroupBy(mission => mission.Label, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => MissionStatusPriority(item.Status)).First().Status, StringComparer.Ordinal);
        source.Add($"missionData={missionData.Count}; tracking={missionData.Count(mission => mission.Status == MissionStatusTracking)}; fulfilled={missionData.Count(mission => mission.Status == MissionStatusFulfilled)}; finished={missionData.Count(mission => mission.Status == MissionStatusFinished)}");

        var trackingMissions = missionData.Count > 0
            ? missionData
                .Where(mission => mission.TrackedMission != null && mission.Status != MissionStatusFinished)
                .Select(mission => mission.TrackedMission)
                .ToList()
            : ReadTrackingMissions(schedulerType, errors).ToList();
        source.Add($"trackingMissions={trackingMissions.Count}");

        var npcKeys = ReadGlobalNpcKeys(dataBaseDayType, errors).ToList();
        source.Add($"npcKeys={npcKeys.Count}");

        var npcPlaceNames = ReadNpcPlaceNames(runtimeDaySceneType, dataBaseDayType, npcKeys, source, errors);
        source.Add($"npcPlaces={npcPlaceNames.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, npcPlaceNames, statusByMission, npcKeys, "InteractMission");

        var trackedNpcLabels = ReadTrackedNpcLabels(runtimeDaySceneType, errors).Except(npcKeys, StringComparer.Ordinal).ToList();
        source.Add($"trackedNpcLabels={trackedNpcLabels.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, npcPlaceNames, statusByMission, trackedNpcLabels, "TrackedNPC");

        var daySceneMapLabels = ReadDaySceneMapCharacterLabels(daySceneMapType, errors)
            .Except(npcKeys.Concat(trackedNpcLabels), StringComparer.Ordinal)
            .ToList();
        source.Add($"daySceneMapCharacters={daySceneMapLabels.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, npcPlaceNames, statusByMission, daySceneMapLabels, "DaySceneMap");

        var sceneCharacterLabels = ReadSceneCharacterLabels(characterComponentType, errors)
            .Except(npcKeys.Concat(trackedNpcLabels).Concat(daySceneMapLabels), StringComparer.Ordinal)
            .ToList();
        source.Add($"sceneCharacters={sceneCharacterLabels.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, npcPlaceNames, statusByMission, sceneCharacterLabels, "SceneCharacter");

        var scheduledEventMissions = ReadScheduledEventMissionFallbackMissions(dataBaseDayType, dataBaseSchedulerType, schedulerType, npcPlaceNames, source, errors).ToList();
        source.Add($"scheduledEventMissions={scheduledEventMissions.Count}");
        missions.AddRange(scheduledEventMissions);

        var trackedInteractableLabels = ReadTrackedInteractableLabels(runtimeDaySceneType, errors).ToList();
        source.Add($"trackedInteractables={trackedInteractableLabels.Count}");
        var trackedInteractableMissions = ReadInteractableMissions(schedulerType, trackingMissions, statusByMission, trackedInteractableLabels, "TrackedInteractable", errors).ToList();
        source.Add($"trackedInteractableMissions={trackedInteractableMissions.Count}");
        missions.AddRange(trackedInteractableMissions);

        var sceneInteractableLabels = ReadSceneInteractableLabels(missionInteractType, errors).Except(trackedInteractableLabels, StringComparer.Ordinal).ToList();
        source.Add($"sceneInteractableKeys={sceneInteractableLabels.Count}");
        var sceneInteractableMissions = ReadInteractableMissions(schedulerType, trackingMissions, statusByMission, sceneInteractableLabels, "SceneInteractable", errors).ToList();
        source.Add($"sceneInteractables={sceneInteractableMissions.Count}");
        missions.AddRange(sceneInteractableMissions);

        var fallbackMissions = ReadTrackingMissionFallbackMissions(dataBaseDayType, schedulerType, npcPlaceNames, trackingMissions, statusByMission).ToList();
        source.Add($"trackingFallback={fallbackMissions.Count}");
        missions.AddRange(fallbackMissions);

        var deduplicated = missions
            .GroupBy(mission => $"{mission.Label}|{mission.CharacterLabel}", StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(mission => MissionStatusPriority(mission.Status))
                .ThenBy(mission => MissionSourcePriority(mission.Source))
                .First())
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

    public static RuntimeMissionContext WithServeTargets(
        RuntimeMissionContext context,
        DataRepository? repository,
        IEnumerable<NightBusinessGuest> activeGuests,
        IEnumerable<NightBusinessOrder> activeOrders)
    {
        var targets = ReadServeTargets(repository, activeGuests, activeOrders).ToList();
        var source = string.IsNullOrWhiteSpace(context.Source)
            ? $"serveTargets={targets.Count}"
            : $"{context.Source}; serveTargets={targets.Count}";

        return new RuntimeMissionContext
        {
            AvailableMissions = context.AvailableMissions,
            ServeTargets = targets,
            Source = source,
            Error = context.Error,
        };
    }

    private static IEnumerable<RuntimeMissionServeTarget> ReadServeTargets(
        DataRepository? repository,
        IEnumerable<NightBusinessGuest> activeGuests,
        IEnumerable<NightBusinessOrder> activeOrders)
    {
        var schedulerType = RuntimeReflectionUtility.FindType(RunTimeSchedulerTypeName);
        if (schedulerType == null) yield break;

        var guestRows = activeGuests
            .Select(guest => (guest.GuestId, guest.GuestName))
            .Concat(activeOrders.Select(order => (order.GuestId, order.GuestName)))
            .Where(row => row.GuestId.HasValue)
            .GroupBy(row => row.GuestId!.Value)
            .Select(group => (GuestId: group.Key, GuestName: group.FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.GuestName)).GuestName ?? ""))
            .OrderBy(row => row.GuestId)
            .ToList();

        foreach (var guest in guestRows)
        {
            if (!TryReadServeMissionRecipe(schedulerType, guest.GuestId, out var recipeId)) continue;
            if (recipeId < 0) continue;

            var recipe = repository?.Recipes.FirstOrDefault(item => item.Id == recipeId);
            yield return new RuntimeMissionServeTarget
            {
                GuestId = guest.GuestId,
                GuestName = string.IsNullOrWhiteSpace(guest.GuestName)
                    ? repository?.RareCustomersById.GetValueOrDefault(guest.GuestId)?.Name ?? ""
                    : guest.GuestName,
                GuestLabel = "",
                MissionLabel = "",
                MissionTitle = "",
                RecipeId = recipeId,
                RecipeName = recipe?.Name ?? "",
                Status = MissionStatusTracking,
                Source = "ContainsSpecialNPCServeInWorkMission",
            };
        }
    }

    private static bool TryReadServeMissionRecipe(Type schedulerType, int guestId, out int recipeId)
    {
        recipeId = -1;
        try
        {
            var method = schedulerType
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "ContainsSpecialNPCServeInWorkMission"
                    && method.GetParameters().Length == 2);
            if (method == null) return false;

            object?[] args = { guestId, 0 };
            var result = method.Invoke(null, args);
            if (!RuntimeReflectionUtility.ToBool(result)) return false;
            recipeId = RuntimeReflectionUtility.ToInt(args[1], -1);
            return recipeId >= 0;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ReadGlobalNpcKeys(Type dataBaseDayType, List<string> errors)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var key in EnumerateTextValues(RuntimeReflectionUtility.InvokeStaticMethod(dataBaseDayType, "GetAllNPCKeys")))
            {
                result.Add(key);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"npcKeys: {ex.Message}");
        }

        foreach (var memberName in new[] { "AllMappedNPCsMapping", "AllNPCsMapping", "allNPCs" })
        {
            try
            {
                foreach (var key in ReadTextKeys(RuntimeReflectionUtility.GetStaticMemberValue(dataBaseDayType, memberName)))
                {
                    result.Add(key);
                }
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"npcKeys {memberName}: {ex.Message}");
            }
        }

        return result.OrderBy(value => value, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> EnumerateTextValues(object? value)
    {
        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(value))
        {
            var text = item?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }

    private static IEnumerable<string> ReadTextKeys(object? dictionary)
    {
        foreach (var key in EnumerateTextValues(RuntimeReflectionUtility.GetMemberValue(dictionary, "Keys")))
        {
            yield return key;
        }

        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(dictionary))
        {
            var key = RuntimeReflectionUtility.GetMemberValue(item, "Key")
                ?? RuntimeReflectionUtility.GetMemberValue(item, "key")
                ?? RuntimeReflectionUtility.GetMemberValue(item, "Item1");
            var text = key?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }

    private static void AddAvailableInteractMissions(
        List<RuntimeMissionInfo> missions,
        List<string> errors,
        Type dataBaseDayType,
        Type schedulerType,
        IReadOnlyDictionary<string, List<string>> npcPlaceNames,
        IReadOnlyDictionary<string, string> statusByMission,
        IEnumerable<string> characterLabels,
        string source)
    {
        foreach (var characterLabel in characterLabels
                     .Where(label => !string.IsNullOrWhiteSpace(label))
                     .Distinct(StringComparer.Ordinal))
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
                    var status = ResolveAvailableInteractMissionStatus(schedulerType, statusByMission, label);

                    missions.Add(new RuntimeMissionInfo
                    {
                        Label = label,
                        Title = ResolveMissionTitle(label),
                        CharacterLabel = characterLabel,
                        CharacterName = ResolveNpcName(dataBaseDayType, characterLabel),
                        Places = ResolvePlaces(dataBaseDayType, npcPlaceNames, characterLabel),
                        Source = source,
                        Status = status,
                        Started = status != MissionStatusAvailable,
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

    private static IEnumerable<MissionDataSnapshot> ReadTrackedMissionDataSnapshots(Type schedulerType, List<string> errors)
    {
        foreach (var trackedMission in ReadTrackingMissions(schedulerType, errors))
        {
            var label = ReadTextMember(trackedMission, "missionLabel");
            if (string.IsNullOrWhiteSpace(label)) continue;

            object? parsed = null;
            try
            {
                parsed = RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "ParseActiveMissionData", trackedMission);
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"mission status {label}: {ex.Message}");
            }

            var parsedMission = ReadTupleItem(parsed, "Item1") ?? trackedMission;
            var statusValue = ReadTupleItem(parsed, "Item2");
            yield return new MissionDataSnapshot
            {
                TrackedMission = parsedMission,
                Label = label,
                Status = ResolveTrackedMissionStatus(parsedMission, trackedMission, statusValue),
            };
        }
    }

    private static string ResolveAvailableInteractMissionStatus(
        Type schedulerType,
        IReadOnlyDictionary<string, string> statusByMission,
        string missionLabel)
    {
        if (statusByMission.TryGetValue(missionLabel, out var status))
        {
            return status == MissionStatusTracking ? MissionStatusFulfilled : status;
        }

        var started = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "HaveMissionStarted", missionLabel));
        return started ? MissionStatusFulfilled : MissionStatusAvailable;
    }

    private static IEnumerable<RuntimeMissionInfo> ReadScheduledEventMissionFallbackMissions(
        Type dataBaseDayType,
        Type? dataBaseSchedulerType,
        Type schedulerType,
        IReadOnlyDictionary<string, List<string>> npcPlaceNames,
        List<string> source,
        List<string> errors)
    {
        if (dataBaseSchedulerType == null) yield break;

        var characterHasInteractEvent = new Dictionary<string, bool>(StringComparer.Ordinal);
        var scheduledEvents = ReadCurrentScheduledEvents(dataBaseSchedulerType, schedulerType, source, errors).ToList();
        if (scheduledEvents.Count == 0) yield break;

        foreach (var scheduledEvent in scheduledEvents)
        {
            var characterLabel = ResolveScheduledEventCharacterLabel(schedulerType, scheduledEvent.Node, characterHasInteractEvent);
            if (string.IsNullOrWhiteSpace(characterLabel)) continue;

            foreach (var missionLabel in ReadPostMissionLabels(scheduledEvent.Node).Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(missionLabel)) continue;

                var finished = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "HaveMissionFinished", missionLabel));
                if (finished) continue;

                var started = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "HaveMissionStarted", missionLabel));
                if (started) continue;

                yield return new RuntimeMissionInfo
                {
                    Label = missionLabel,
                    Title = ResolveMissionTitle(missionLabel),
                    CharacterLabel = characterLabel,
                    CharacterName = ResolveNpcName(dataBaseDayType, characterLabel),
                    Places = ResolvePlaces(dataBaseDayType, npcPlaceNames, characterLabel),
                    Source = string.IsNullOrWhiteSpace(scheduledEvent.Label)
                        ? "ScheduledEventMission"
                        : $"ScheduledEventMission:{scheduledEvent.Label}",
                    Status = MissionStatusAvailable,
                    Started = false,
                    Finished = false,
                };
            }
        }
    }

    private static IEnumerable<ScheduledEventSnapshot> ReadCurrentScheduledEvents(
        Type dataBaseSchedulerType,
        Type schedulerType,
        List<string> source,
        List<string> errors)
    {
        var result = new Dictionary<string, ScheduledEventSnapshot>(StringComparer.Ordinal);

        foreach (var label in ReadCurrentScheduledEventLabels(schedulerType, source, errors))
        {
            if (string.IsNullOrWhiteSpace(label) || result.ContainsKey(label)) continue;

            var eventNode = RuntimeReflectionUtility.InvokeStaticMethod(dataBaseSchedulerType, "RefEvent", label);
            if (eventNode == null) continue;
            result[label] = new ScheduledEventSnapshot { Label = label, Node = eventNode };
        }

        var directCount = 0;
        foreach (var eventNode in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "AllScheduledEventToday")))
        {
            if (eventNode == null) continue;
            directCount++;
            var label = ReadTextMember(eventNode, "label") ?? eventNode.ToString() ?? "";
            var key = string.IsNullOrWhiteSpace(label) ? $"direct:{directCount}" : label;
            result.TryAdd(key, new ScheduledEventSnapshot { Label = label, Node = eventNode });
        }

        source.Add($"scheduledEvents={result.Count}; directEvents={directCount}");
        return result.Values;
    }

    private static IEnumerable<string> ReadCurrentScheduledEventLabels(Type schedulerType, List<string> source, List<string> errors)
    {
        var scheduledEvents = RuntimeReflectionUtility.GetStaticMemberValue(schedulerType, "scheduledEvents");
        if (scheduledEvents == null)
        {
            source.Add("scheduledEventLabels=0");
            yield break;
        }

        var count = 0;
        foreach (var day in ReadCurrentEventDays())
        {
            var labels = RuntimeReflectionUtility.InvokeMethod(scheduledEvents, "get_Item", day);
            foreach (var label in EnumerateTextValues(labels))
            {
                count++;
                yield return label;
            }
        }

        source.Add($"scheduledEventLabels={count}");
    }

    private static IEnumerable<int> ReadCurrentEventDays()
    {
        var days = new HashSet<int> { -1 };
        var runtimePlayerDataType = RuntimeReflectionUtility.FindType(RunTimePlayerDataTypeName);
        var day = runtimePlayerDataType == null
            ? null
            : RuntimeReflectionUtility.InvokeStaticMethod(runtimePlayerDataType, "GetDay");
        var correctedDay = RuntimeReflectionUtility.ToInt(RuntimeReflectionUtility.GetMemberValue(day, "CorrectedDay"), int.MinValue);
        if (correctedDay != int.MinValue) days.Add(correctedDay);
        return days.OrderBy(value => value).ToList();
    }

    private static string? ResolveScheduledEventCharacterLabel(
        Type schedulerType,
        object? eventNode,
        Dictionary<string, bool> characterHasInteractEvent)
    {
        var scheduledEvent = RuntimeReflectionUtility.GetMemberValue(eventNode, "scheduledEvent");
        var trigger = RuntimeReflectionUtility.GetMemberValue(scheduledEvent, "trigger");
        var triggerId = ReadTextMember(trigger, "triggerId");
        if (string.IsNullOrWhiteSpace(triggerId)) return null;

        var triggerType = RuntimeReflectionUtility.GetMemberValue(trigger, "triggerType");
        var triggerTypeInt = RuntimeReflectionUtility.ToInt(triggerType, int.MinValue);
        var triggerTypeText = triggerType?.ToString() ?? "";
        if (triggerTypeInt == 3 || triggerTypeText.Contains("OnTalkWithCharacter", StringComparison.OrdinalIgnoreCase))
        {
            return triggerId;
        }

        if (triggerTypeInt != 5 && !triggerTypeText.Contains("KizunaCheckPoint", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!characterHasInteractEvent.TryGetValue(triggerId, out var hasEvent))
        {
            hasEvent = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "CheckCharacterInteractEvent", triggerId));
            characterHasInteractEvent[triggerId] = hasEvent;
        }

        return hasEvent ? triggerId : null;
    }

    private static IEnumerable<string> ReadPostMissionLabels(object? eventNode)
    {
        foreach (var memberName in new[] { "postMissions", "postMissionsAfterPerformance" })
        {
            foreach (var label in EnumerateTextValues(RuntimeReflectionUtility.GetMemberValue(eventNode, memberName)))
            {
                yield return label;
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

    private static IReadOnlyDictionary<string, List<string>> ReadNpcPlaceNames(
        Type? runtimeDaySceneType,
        Type dataBaseDayType,
        IEnumerable<string> npcKeys,
        List<string> source,
        List<string> errors)
    {
        var result = ReadTrackedNpcPlaceNames(runtimeDaySceneType, dataBaseDayType, errors)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        source.Add($"runtimeNpcPlaces={result.Count}");

        var staticCount = 0;
        foreach (var pair in ReadStaticNpcPlaceNames(dataBaseDayType, npcKeys, errors))
        {
            if (!result.TryGetValue(pair.Key, out var places))
            {
                places = new HashSet<string>(StringComparer.Ordinal);
                result[pair.Key] = places;
            }

            var before = places.Count;
            foreach (var place in pair.Value)
            {
                places.Add(place);
            }

            if (places.Count > before) staticCount++;
        }

        source.Add($"staticNpcPlaces={staticCount}");
        return result.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, List<string>> ReadTrackedNpcPlaceNames(Type? runtimeDaySceneType, Type dataBaseDayType, List<string> errors)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (runtimeDaySceneType == null) return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);

        object? trackedNpcMaps;
        try
        {
            trackedNpcMaps = RuntimeReflectionUtility.GetStaticMemberValue(runtimeDaySceneType, "trackedNPCs");
        }
        catch (Exception ex)
        {
            if (errors.Count < 8) errors.Add($"npc places: {ex.Message}");
            return result.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);
        }

        foreach (var mapCandidate in RuntimeReflectionUtility.EnumerateObjects(trackedNpcMaps))
        {
            var mapLabel = ReadTextMember(mapCandidate, "Key");
            var npcMap = RuntimeReflectionUtility.NormalizeKeyValueValue(mapCandidate);
            foreach (var npcCandidate in RuntimeReflectionUtility.EnumerateObjects(npcMap))
            {
                try
                {
                    var npc = RuntimeReflectionUtility.NormalizeKeyValueValue(npcCandidate);
                    var key = ReadTextMember(npc, "key")
                        ?? ReadTextMember(npcCandidate, "Key")
                        ?? ReadTextMember(npcCandidate, "key");
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    foreach (var place in ResolveTrackedNpcPlaces(dataBaseDayType, npc, mapLabel))
                    {
                        if (!result.TryGetValue(key, out var places))
                        {
                            places = new HashSet<string>(StringComparer.Ordinal);
                            result[key] = places;
                        }

                        places.Add(place);
                    }
                }
                catch (Exception ex)
                {
                    if (errors.Count < 8) errors.Add($"npc place item: {ex.Message}");
                }
            }
        }

        return result.ToDictionary(pair => pair.Key, pair => pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, List<string>> ReadStaticNpcPlaceNames(
        Type dataBaseDayType,
        IEnumerable<string> npcKeys,
        List<string> errors)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var key in npcKeys.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var npc = RuntimeReflectionUtility.InvokeStaticMethod(dataBaseDayType, "RefNPC", key);
                foreach (var place in ResolveStaticNpcPlaces(dataBaseDayType, npc))
                {
                    if (!result.TryGetValue(key, out var places))
                    {
                        places = new HashSet<string>(StringComparer.Ordinal);
                        result[key] = places;
                    }

                    places.Add(place);
                }
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"static npc place {key}: {ex.Message}");
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> ResolveStaticNpcPlaces(Type dataBaseDayType, object? npc)
    {
        foreach (var destination in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(npc, "possibleDestinations")))
        {
            var spawnMarker = ReadTextMember(destination, "spawnMarker");
            if (string.IsNullOrWhiteSpace(spawnMarker)) continue;

            var mapLabel = RuntimeReflectionUtility.InvokeStaticMethod(dataBaseDayType, "GetMapLabelFromSpawnMarker", spawnMarker)?.ToString();
            if (string.IsNullOrWhiteSpace(mapLabel)) continue;

            var normalized = NormalizePlaceName(mapLabel);
            if (!string.IsNullOrWhiteSpace(normalized)) yield return normalized;
        }
    }

    private static IEnumerable<string> ResolveTrackedNpcPlaces(Type dataBaseDayType, object? npc, string? mapLabel)
    {
        foreach (var label in ResolvePlaceLabelCandidates(dataBaseDayType, npc, mapLabel))
        {
            var normalized = NormalizePlaceName(label);
            if (!string.IsNullOrWhiteSpace(normalized)) yield return normalized;
        }
    }

    private static IEnumerable<string> ResolvePlaceLabelCandidates(Type dataBaseDayType, object? npc, string? mapLabel)
    {
        if (!string.IsNullOrWhiteSpace(mapLabel)) yield return mapLabel;

        var overridePosition = RuntimeReflectionUtility.GetMemberValue(npc, "overridePosition");
        var overrideMapLabel = ReadTextMember(overridePosition, "mapLabel");
        if (!string.IsNullOrWhiteSpace(overrideMapLabel)) yield return overrideMapLabel;

        var currentDestination = RuntimeReflectionUtility.GetMemberValue(npc, "currentDestination");
        var spawnMarker = ReadTextMember(currentDestination, "spawnMarker");
        if (!string.IsNullOrWhiteSpace(spawnMarker))
        {
            var destinationMapLabel = RuntimeReflectionUtility.InvokeStaticMethod(dataBaseDayType, "GetMapLabelFromSpawnMarker", spawnMarker)?.ToString();
            if (!string.IsNullOrWhiteSpace(destinationMapLabel)) yield return destinationMapLabel;
        }
    }

    private static List<string> ResolvePlaces(Type dataBaseDayType, IReadOnlyDictionary<string, List<string>> npcPlaceNames, string characterLabel)
    {
        if (string.IsNullOrWhiteSpace(characterLabel)) return new List<string>();
        if (npcPlaceNames.TryGetValue(characterLabel, out var places) && places.Count > 0) return places;

        var npc = RuntimeReflectionUtility.InvokeStaticMethod(dataBaseDayType, "RefNPC", characterLabel);
        return ResolveStaticNpcPlaces(dataBaseDayType, npc)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizePlaceName(string label)
    {
        var languageType = RuntimeReflectionUtility.FindType(DaySceneLanguageTypeName);
        var language = languageType == null
            ? null
            : RuntimeReflectionUtility.InvokeStaticMethod(languageType, "GetMapLanguageData", label);
        var localized = ReadTextLikeValue(language);
        return string.IsNullOrWhiteSpace(localized) ? label : localized;
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
        IReadOnlyDictionary<string, string> statusByMission,
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
            var status = GetMissionStatus(statusByMission, missionLabel, MissionStatusTracking);

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
                    Status = status,
                    Started = status != MissionStatusAvailable,
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
                Status = MissionStatusAvailable,
                Started = false,
                Finished = false,
            };
        }
    }

    private static IEnumerable<RuntimeMissionInfo> ReadTrackingMissionFallbackMissions(
        Type dataBaseDayType,
        Type schedulerType,
        IReadOnlyDictionary<string, List<string>> npcPlaceNames,
        IReadOnlyList<object?> trackingMissions,
        IReadOnlyDictionary<string, string> statusByMission)
    {
        foreach (var trackedMission in trackingMissions)
        {
            var missionLabel = ReadTextMember(trackedMission, "missionLabel");
            if (string.IsNullOrWhiteSpace(missionLabel)) continue;

            var finished = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "HaveMissionFinished", missionLabel));
            if (finished) continue;
            var status = GetMissionStatus(statusByMission, missionLabel, MissionStatusTracking);

            var conditions = EnumerateConditionSnapshots(trackedMission).ToList();
            if (status == MissionStatusTracking && conditions.Count > 0 && conditions.All(condition => condition.Finished))
            {
                status = MissionStatusFulfilled;
            }

            var firstUnfinished = conditions.FirstOrDefault(condition => !condition.Finished);
            var talkCondition = conditions.FirstOrDefault(condition => IsConditionType(condition.Condition, "TalkWithCharacter", 1));
            var serveCondition = conditions.FirstOrDefault(condition => IsConditionType(condition.Condition, "ServeInWork", 4) && !condition.Finished)
                ?? conditions.FirstOrDefault(condition => IsConditionType(condition.Condition, "ServeInWork", 4));
            var missionReceiver = ReadMissionReceiver(trackedMission);

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
                        Places = ResolvePlaces(dataBaseDayType, npcPlaceNames, characterLabel),
                        Source = "TrackingMission:PendingTalk",
                        Status = status,
                        Started = status != MissionStatusAvailable,
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
                        Status = status,
                        Started = status != MissionStatusAvailable,
                        Finished = finished,
                    };
                    continue;
                }
            }

            var activeCharacterLabel = ReadTextMember(firstUnfinished?.Condition, "label")
                ?? ReadTextMember(serveCondition?.Condition, "label")
                ?? missionReceiver
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
                Places = ResolvePlaces(dataBaseDayType, npcPlaceNames, activeCharacterLabel),
                Source = firstUnfinished == null ? "TrackingMission:Tracked" : "TrackingMission:Active",
                Status = status,
                Started = status != MissionStatusAvailable,
                Finished = finished,
                TargetRecipeId = ReadServeConditionRecipeId(serveCondition?.Condition),
                TargetRecipeName = ResolveRecipeName(ReadServeConditionRecipeId(serveCondition?.Condition)),
            };
        }
    }

    private static IEnumerable<MissionConditionSnapshot> EnumerateConditionSnapshots(object? trackedMission)
    {
        if (trackedMission == null) yield break;

        RefreshTrackedMissionFinishStates(trackedMission);

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

    private static string ResolveTrackedMissionStatus(object? parsedMission, object? originalMission, object? statusValue)
    {
        var parsedStatus = statusValue == null ? null : NormalizeMissionStatus(statusValue);
        if (parsedStatus == MissionStatusFinished) return MissionStatusFinished;

        if (IsTrackedMissionFulfilled(parsedMission) || IsTrackedMissionFulfilled(originalMission))
        {
            return MissionStatusFulfilled;
        }

        return parsedStatus ?? MissionStatusTracking;
    }

    private static bool IsTrackedMissionFulfilled(object? trackedMission)
    {
        if (trackedMission == null) return false;

        RefreshTrackedMissionFinishStates(trackedMission);

        foreach (var memberName in new[] { "HasFulfilled", "get_HasFulfilled", "HasFullfilled", "get_HasFullfilled", "IsFulfilled", "isFulfilled" })
        {
            var value = memberName.StartsWith("get_", StringComparison.Ordinal)
                ? RuntimeReflectionUtility.InvokeMethod(trackedMission, memberName)
                : RuntimeReflectionUtility.GetMemberValue(trackedMission, memberName);
            if (RuntimeReflectionUtility.ToBool(value)) return true;
        }

        var finishStates = RuntimeReflectionUtility
            .EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(trackedMission, "conditionFinishStates"))
            .Select(RuntimeReflectionUtility.ToBool)
            .ToList();
        return finishStates.Count > 0 && finishStates.All(state => state);
    }

    private static void RefreshTrackedMissionFinishStates(object? trackedMission)
    {
        try
        {
            RuntimeReflectionUtility.InvokeMethod(trackedMission, "UpdateFinishStates");
        }
        catch
        {
            // Best-effort only. The following field/property reads still provide a safe fallback.
        }
    }

    private static object? ReadTupleItem(object? tuple, string itemName)
    {
        if (tuple == null) return null;
        return RuntimeReflectionUtility.GetMemberValue(tuple, itemName)
            ?? RuntimeReflectionUtility.GetMemberValue(tuple, $"m_{itemName}")
            ?? RuntimeReflectionUtility.GetMemberValue(tuple, $"_{itemName}");
    }

    private static string NormalizeMissionStatus(object? statusValue)
    {
        var statusText = statusValue?.ToString() ?? "";
        if (statusText.Contains("Finished", StringComparison.OrdinalIgnoreCase)
            || statusText.Contains("Done", StringComparison.OrdinalIgnoreCase))
        {
            return MissionStatusFinished;
        }

        if (statusText.Contains("Fulfilled", StringComparison.OrdinalIgnoreCase)
            || statusText.Contains("Fullfilled", StringComparison.OrdinalIgnoreCase)
            || statusText.Contains("Complete", StringComparison.OrdinalIgnoreCase))
        {
            return MissionStatusFulfilled;
        }

        if (statusText.Contains("Tracking", StringComparison.OrdinalIgnoreCase)
            || statusText.Contains("Active", StringComparison.OrdinalIgnoreCase))
        {
            return MissionStatusTracking;
        }

        var statusNumber = RuntimeReflectionUtility.GetMemberValue(statusValue, "value__")
            ?? RuntimeReflectionUtility.GetMemberValue(statusValue, "m_value")
            ?? RuntimeReflectionUtility.GetMemberValue(statusValue, "value")
            ?? statusValue;
        var statusInt = RuntimeReflectionUtility.ToInt(statusNumber, int.MinValue);
        if (statusInt == 0) return MissionStatusTracking;
        if (statusInt == 1) return MissionStatusFulfilled;
        if (statusInt == 2) return MissionStatusFinished;
        return MissionStatusTracking;
    }

    private static string GetMissionStatus(IReadOnlyDictionary<string, string> statusByMission, string label, string fallback)
    {
        return statusByMission.TryGetValue(label, out var status) ? status : fallback;
    }

    private static int MissionStatusPriority(string status)
    {
        return status switch
        {
            MissionStatusFulfilled => 4,
            MissionStatusTracking => 3,
            MissionStatusAvailable => 2,
            MissionStatusFinished => 1,
            _ => 0,
        };
    }

    private static int MissionSourcePriority(string source)
    {
        if (source.StartsWith("TrackingMission", StringComparison.Ordinal)) return 0;
        if (source.Contains("Interactable", StringComparison.Ordinal)) return 1;
        return 2;
    }

    private static string? ReadMissionReceiver(object? trackedMission)
    {
        var mission = RuntimeReflectionUtility.InvokeMethod(trackedMission, "GetMissionReference");
        return ReadTextMember(mission, "reciever")
            ?? ReadTextMember(mission, "receiver");
    }

    private static int? ReadServeConditionRecipeId(object? condition)
    {
        if (condition == null) return null;

        foreach (var member in new[] { "amount", "product", "foodId", "recipeId" })
        {
            var value = RuntimeReflectionUtility.GetMemberValue(condition, member);
            var id = RuntimeReflectionUtility.ToInt(value, int.MinValue);
            if (id >= 0) return id;
        }

        return null;
    }

    private static string? ResolveRecipeName(int? recipeId)
    {
        if (!recipeId.HasValue || recipeId.Value < 0) return null;

        var languageType = RuntimeReflectionUtility.FindType(DataBaseLanguageTypeName);
        var language = languageType == null
            ? null
            : RuntimeReflectionUtility.InvokeStaticMethod(languageType, "GetFoodLang", recipeId.Value);
        var text = ReadTextLikeValue(language);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private sealed class MissionConditionSnapshot
    {
        public object? Condition { get; init; }
        public bool Finished { get; init; }
    }

    private sealed class MissionDataSnapshot
    {
        public object? TrackedMission { get; init; }
        public string Label { get; init; } = "";
        public string Status { get; init; } = MissionStatusTracking;
    }

    private sealed class ScheduledEventSnapshot
    {
        public string Label { get; init; } = "";
        public object? Node { get; init; }
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
        if (string.IsNullOrWhiteSpace(npcKey)) return "";

        var daySceneLanguageType = RuntimeReflectionUtility.FindType(DaySceneLanguageTypeName);
        var daySceneName = daySceneLanguageType == null
            ? null
            : RuntimeReflectionUtility.InvokeStaticMethod(daySceneLanguageType, "RefDaySceneName", npcKey)?.ToString();
        if (IsUsableLocalizedText(daySceneName, npcKey)) return daySceneName!.Trim();

        var npc = RuntimeReflectionUtility.InvokeStaticMethod(dataBaseDayType, "RefNPC", npcKey);
        var identity = RuntimeReflectionUtility.GetMemberValue(npc, "identity");
        var text = ReadTextLikeValue(RuntimeReflectionUtility.InvokeMethod(identity, "GetLanguageData"));
        if (!string.IsNullOrWhiteSpace(text)) return text;

        text = ReadTextLikeValue(RuntimeReflectionUtility.GetMemberValue(npc, "Text"));
        return string.IsNullOrWhiteSpace(text) ? npcKey : text;
    }

    private static string ReadTextLikeValue(object? value)
    {
        if (value == null) return "";

        foreach (var member in new[] { "Name", "name", "Title", "title", "Text", "text", "Value", "value", "Description", "description", "Chinese", "Zh", "zh" })
        {
            try
            {
                var memberValue = NormalizeDisplayText(RuntimeReflectionUtility.GetMemberValue(value, member));
                if (!string.IsNullOrWhiteSpace(memberValue)) return memberValue;
            }
            catch
            {
                // Try the next common text member.
            }
        }

        try
        {
            return NormalizeDisplayText(value) ?? "";
        }
        catch
        {
            // Ignore conversion failures.
        }

        return "";
    }

    private static bool IsUsableLocalizedText(string? text, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (string.Equals(text, sourceLabel, StringComparison.Ordinal)) return false;
        return !LooksLikeRuntimeTypeName(text);
    }

    private static string? NormalizeDisplayText(object? value)
    {
        if (value == null) return null;

        string? text;
        try
        {
            text = value.ToString();
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return LooksLikeRuntimeTypeName(text) ? null : text;
    }

    private static bool LooksLikeRuntimeTypeName(string text)
    {
        if (text.Contains("LanguageBase", StringComparison.Ordinal)) return true;
        if (text.StartsWith("GameData.", StringComparison.Ordinal)) return true;
        if (text.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        if (text.StartsWith("System.", StringComparison.Ordinal)) return true;
        return false;
    }
}
