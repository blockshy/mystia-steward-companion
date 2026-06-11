using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

public static class RuntimeMissionSnapshotService
{
    private const string DataBaseDayTypeName = "GameData.Core.Collections.DaySceneUtility.DataBaseDay";
    private const string RunTimeSchedulerTypeName = "GameData.RunTime.Common.RunTimeScheduler";
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string RunTimeDaySceneTypeName = "GameData.RunTime.DaySceneUtility.RunTimeDayScene";
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

        var npcKeys = ReadGlobalNpcKeys(dataBaseDayType, errors).ToList();
        source.Add($"npcKeys={npcKeys.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, npcKeys, "InteractMission");

        var trackedNpcLabels = ReadTrackedNpcLabels(errors).Except(npcKeys, StringComparer.Ordinal).ToList();
        source.Add($"trackedNpcLabels={trackedNpcLabels.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, trackedNpcLabels, "TrackedNPC");

        var sceneCharacterLabels = ReadSceneCharacterLabels(errors).Except(npcKeys.Concat(trackedNpcLabels), StringComparer.Ordinal).ToList();
        source.Add($"sceneCharacters={sceneCharacterLabels.Count}");
        AddAvailableInteractMissions(missions, errors, dataBaseDayType, schedulerType, sceneCharacterLabels, "SceneCharacter");

        var sceneInteractableMissions = ReadSceneInteractableMissions(errors).ToList();
        source.Add($"sceneInteractables={sceneInteractableMissions.Count}");
        missions.AddRange(sceneInteractableMissions);

        var deduplicated = missions
            .GroupBy(mission => $"{mission.Label}|{mission.CharacterLabel}|{mission.Source}", StringComparer.Ordinal)
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
                    var started = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "HaveMissionStarted", label));
                    var finished = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeStaticMethod(schedulerType, "HaveMissionFinished", label));
                    if (started || finished) continue;

                    missions.Add(new RuntimeMissionInfo
                    {
                        Label = label,
                        Title = ResolveMissionTitle(label),
                        CharacterLabel = characterLabel,
                        CharacterName = ResolveNpcName(dataBaseDayType, characterLabel),
                        Source = source,
                        Started = started,
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

    private static IEnumerable<string> ReadTrackedNpcLabels(List<string> errors)
    {
        var runtimeDaySceneType = RuntimeReflectionUtility.FindType(RunTimeDaySceneTypeName);
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

    private static IEnumerable<string> ReadSceneCharacterLabels(List<string> errors)
    {
        var characterComponentType = RuntimeReflectionUtility.FindType(CharacterConditionComponentTypeName);
        if (characterComponentType == null) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in RuntimeReflectionUtility.FindUnityObjects(characterComponentType))
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

    private static IEnumerable<RuntimeMissionInfo> ReadSceneInteractableMissions(List<string> errors)
    {
        var missionInteractType = RuntimeReflectionUtility.FindType(MissionInteractConditionComponentTypeName);
        if (missionInteractType == null) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in RuntimeReflectionUtility.FindUnityObjects(missionInteractType))
        {
            string? interactableKey = null;
            var available = true;
            try
            {
                available = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.GetMemberValue(component, "CheckAvailability"));
                interactableKey = ReadTextMember(component, "interactableKey");
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"scene interactable: {ex.Message}");
            }

            if (!available || string.IsNullOrWhiteSpace(interactableKey) || !seen.Add(interactableKey)) continue;

            yield return new RuntimeMissionInfo
            {
                Label = interactableKey,
                Title = ResolveMissionTitle(interactableKey),
                CharacterLabel = interactableKey,
                CharacterName = "场景交互",
                Source = "SceneInteractable",
                Started = false,
                Finished = false,
            };
        }
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
