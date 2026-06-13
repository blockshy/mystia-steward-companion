using System.Reflection;
using BepInEx.Logging;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal sealed class RareGuestInvitationResult
{
    public bool Ok { get; set; }
    public bool RuntimeAvailable { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public int CandidateCount { get; set; }
    public int UsableCount { get; set; }
    public int ExistingSlotCount { get; set; }
    public int ExistingControlledCount { get; set; }
    public int ScheduledSlotCount { get; set; }
    public int InvitedCount { get; set; }
    public int SkippedCount { get; set; }
    public string Source { get; set; } = "";
    public string Diagnostics { get; set; } = "";
    public List<RareGuestInvitationEntry> Available { get; set; } = new();
    public List<RareGuestInvitationEntry> Invited { get; set; } = new();
    public List<RareGuestInvitationEntry> Skipped { get; set; } = new();
}

internal sealed class RareGuestInvitationEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string RuntimeName { get; set; } = "";
    public string Reason { get; set; } = "";
}

internal static class RuntimeRareGuestInvitationService
{
    private const string DataBaseCharacterTypeName = "GameData.Core.Collections.CharacterUtility.DataBaseCharacter";
    private const string DataBaseDayTypeName = "GameData.Core.Collections.DaySceneUtility.DataBaseDay";
    private const string RunTimeAlbumTypeName = "GameData.RunTime.Common.RunTimeAlbum";
    private const string StatusTrackerTypeName = "GameData.RunTime.Common.StatusTracker";
    private const string RuntimeDaySceneTypeName = "GameData.RunTime.DaySceneUtility.RunTimeDayScene";
    private const string DaySceneMapTypeName = "DayScene.DaySceneMap";
    private const string DaySceneSceneManagerTypeName = "DayScene.SceneManager";
    private const string CharacterConditionComponentTypeName = "DayScene.Interactables.Collections.ConditionComponents.CharacterConditionComponent";

    public static RareGuestInvitationResult ListAvailable(DataRepository? repository, ManualLogSource? log)
    {
        try
        {
            return ListAvailableCore(repository, log);
        }
        catch (Exception ex)
        {
            return new RareGuestInvitationResult
            {
                Ok = false,
                RuntimeAvailable = false,
                Status = "读取可邀请稀客失败。",
                Error = ex.Message,
            };
        }
    }

    public static RareGuestInvitationResult InviteOne(DataRepository? repository, int guestId, ManualLogSource? log)
    {
        try
        {
            return InviteOneCore(repository, guestId, log);
        }
        catch (Exception ex)
        {
            return new RareGuestInvitationResult
            {
                Ok = false,
                RuntimeAvailable = false,
                Status = "稀客邀请失败。",
                Error = ex.Message,
            };
        }
    }

    public static RareGuestInvitationResult InviteAllAvailable(DataRepository? repository, ManualLogSource? log)
    {
        try
        {
            return InviteAllAvailableCore(repository, log);
        }
        catch (Exception ex)
        {
            return new RareGuestInvitationResult
            {
                Ok = false,
                RuntimeAvailable = false,
                Status = "稀客邀请失败。",
                Error = ex.Message,
            };
        }
    }

    private static RareGuestInvitationResult ListAvailableCore(DataRepository? repository, ManualLogSource? log)
    {
        var context = ReadInvitationContext(repository);
        if (!context.Ok) return context.Result;

        var result = CreateBaseResult(context);
        foreach (var candidate in context.Candidates)
        {
            ProcessCandidate(result, context.AlbumType!, context.StatusTracker!, candidate, writeInvitation: false);
        }

        result.Ok = true;
        result.InvitedCount = result.Invited.Count;
        result.SkippedCount = result.Skipped.Count;
        result.Status = BuildListStatus(result, context.Source);
        log?.LogInfo($"List inviteable rare guests: {result.Status} source={context.Source}, diagnostics={context.Diagnostics}, candidates={result.CandidateCount}, available={result.Available.Count}, skipped={result.SkippedCount}");
        return result;
    }

    private static RareGuestInvitationResult InviteOneCore(DataRepository? repository, int guestId, ManualLogSource? log)
    {
        if (guestId < 0)
        {
            return Fail("稀客 ID 无效。");
        }

        var context = ReadInvitationContext(repository);
        if (!context.Ok) return context.Result;

        var result = CreateBaseResult(context);
        var target = context.Candidates.FirstOrDefault(candidate => candidate.Id == guestId);
        if (target == null)
        {
            result.Ok = false;
            result.RuntimeAvailable = true;
            result.Status = "当前场景未找到该稀客或该稀客当前不可邀请。";
            result.Error = result.Status;
            result.SkippedCount = result.Skipped.Count;
            return result;
        }

        ProcessCandidate(result, context.AlbumType!, context.StatusTracker!, target, writeInvitation: true);
        foreach (var candidate in context.Candidates.Where(candidate => candidate.Id != guestId))
        {
            ProcessCandidate(result, context.AlbumType!, context.StatusTracker!, candidate, writeInvitation: false);
        }

        result.InvitedCount = result.Invited.Count;
        result.SkippedCount = result.Skipped.Count;
        result.Ok = result.Invited.Count > 0 || result.ExistingControlledCount > 0;
        result.Status = result.Invited.Count > 0
            ? $"已邀请 {result.Invited[0].Name}。"
            : BuildStatus(result, context.Source);
        if (!result.Ok)
        {
            result.Error = result.Skipped.FirstOrDefault(entry => entry.Id == guestId)?.Reason ?? result.Status;
        }

        log?.LogInfo($"Invite rare guest {guestId}: {result.Status} source={context.Source}, diagnostics={context.Diagnostics}, available={result.Available.Count}, invited={result.InvitedCount}, skipped={result.SkippedCount}");
        return result;
    }

    private static RareGuestInvitationResult InviteAllAvailableCore(DataRepository? repository, ManualLogSource? log)
    {
        var context = ReadInvitationContext(repository);
        if (!context.Ok) return context.Result;

        var result = CreateBaseResult(context);
        foreach (var candidate in context.Candidates)
        {
            ProcessCandidate(result, context.AlbumType!, context.StatusTracker!, candidate, writeInvitation: true);
        }

        result.Ok = true;
        result.InvitedCount = result.Invited.Count;
        result.SkippedCount = result.Skipped.Count;
        result.Status = BuildStatus(result, context.Source);
        log?.LogInfo($"Invite all rare guests: {result.Status} source={context.Source}, diagnostics={context.Diagnostics}, candidates={result.CandidateCount}, eligible={result.UsableCount}, existingInvited={result.ExistingControlledCount}, invited={result.InvitedCount}, skipped={result.SkippedCount}");
        return result;
    }

    private static InvitationContext ReadInvitationContext(DataRepository? repository)
    {
        var dataBaseCharacterType = RuntimeReflectionUtility.FindType(DataBaseCharacterTypeName);
        var albumType = RuntimeReflectionUtility.FindType(RunTimeAlbumTypeName);
        var statusTrackerType = RuntimeReflectionUtility.FindType(StatusTrackerTypeName);
        if (dataBaseCharacterType == null || albumType == null || statusTrackerType == null)
        {
            return InvitationContext.Failed(Fail("游戏原生羁绊邀请系统尚未初始化。请在读取存档后的日间场景再试。"));
        }

        var statusTracker = RuntimeReflectionUtility.GetSingletonInstance(statusTrackerType)
            ?? GetGenericSingletonInstance(statusTrackerType)
            ?? RuntimeReflectionUtility.FindUnityObject(statusTrackerType);
        if (statusTracker == null)
        {
            return InvitationContext.Failed(Fail("未读取到游戏邀请状态。请在读取存档后的日间场景再试。"));
        }

        var catalog = repository == null ? null : new RuntimeMappedGuestCatalog(repository);
        var candidates = ReadInviteCandidates(dataBaseCharacterType, catalog, out var source, out var diagnostics);
        if (candidates.Count == 0)
        {
            var failed = Fail("未读取到当前日间场景可邀请稀客，已取消邀请以避免全量误邀。请确认角色已出现在日间场景后再试。");
            failed.RuntimeAvailable = true;
            failed.Source = source;
            failed.Diagnostics = diagnostics;
            return InvitationContext.Failed(failed);
        }

        return new InvitationContext
        {
            Ok = true,
            Result = new RareGuestInvitationResult(),
            AlbumType = albumType,
            StatusTracker = statusTracker,
            Candidates = candidates,
            Source = source,
            Diagnostics = diagnostics,
        };
    }

    private static RareGuestInvitationResult CreateBaseResult(InvitationContext context)
    {
        return new RareGuestInvitationResult
        {
            RuntimeAvailable = true,
            CandidateCount = context.Candidates.Count,
            Source = context.Source,
            Diagnostics = context.Diagnostics,
            ExistingSlotCount = RuntimeReflectionUtility.CountObjects(RuntimeReflectionUtility.GetMemberValue(context.StatusTracker, "InvitedGuests")),
        };
    }

    private static void ProcessCandidate(
        RareGuestInvitationResult result,
        Type albumType,
        object statusTracker,
        InviteCandidate candidate,
        bool writeInvitation)
    {
        if (candidate.Id < 0)
        {
            AddSkipped(result, candidate, "未读取到稀客 ID");
            return;
        }

        if (HasNpcInvited(statusTracker, candidate.Id))
        {
            result.ExistingControlledCount++;
            AddSkipped(result, candidate, "今晚已邀请");
            return;
        }

        var level = RuntimeReflectionUtility.ToInt(
            InvokeStaticMethodWithSingleParameter(albumType, "GetOrGenerateSpecialNPCKizunaLevel", typeof(int), candidate.Id),
            0);
        if (level < 2)
        {
            AddSkipped(result, candidate, $"羁绊等级不足 {level}");
            return;
        }

        if (!HasInviteDialog(candidate.Guest, level, succeed: true))
        {
            AddSkipped(result, candidate, $"当前羁绊等级无成功邀请对话 {level}");
            return;
        }

        result.UsableCount++;
        if (!writeInvitation)
        {
            result.Available.Add(new RareGuestInvitationEntry
            {
                Id = candidate.Id,
                Name = candidate.DisplayName,
                RuntimeName = candidate.RuntimeName,
                Reason = $"可邀请（羁绊 {level}）",
            });
            return;
        }

        RuntimeReflectionUtility.InvokeMethod(statusTracker, "RecordInvitedGuest", candidate.Id);
        if (!HasNpcInvited(statusTracker, candidate.Id))
        {
            AddSkipped(result, candidate, "原生记录邀请失败");
            return;
        }

        result.Invited.Add(new RareGuestInvitationEntry
        {
            Id = candidate.Id,
            Name = candidate.DisplayName,
            RuntimeName = candidate.RuntimeName,
            Reason = $"已按原生羁绊邀请条件加入今晚名单（羁绊 {level}）",
        });
    }

    private static RareGuestInvitationResult Fail(string message)
    {
        return new RareGuestInvitationResult
        {
            Ok = false,
            RuntimeAvailable = false,
            Status = message,
            Error = message,
        };
    }

    private static IReadOnlyList<InviteCandidate> ReadInviteCandidates(
        Type dataBaseCharacterType,
        RuntimeMappedGuestCatalog? catalog,
        out string source,
        out string diagnostics)
    {
        source = "current-day-scene";
        var labels = ReadCurrentDaySceneNpcLabels(out diagnostics).ToList();
        var candidates = labels
            .Select(label => new { Label = label, Guest = InvokeStaticMethodWithSingleParameter(dataBaseCharacterType, "RefSGuest", typeof(string), label) })
            .Where(item => item.Guest != null)
            .Select(item => BuildCandidate(catalog, item.Guest!, "current-day-scene"))
            .Where(candidate => candidate != null)
            .Select(candidate => candidate!)
            .ToList();
        diagnostics = $"{diagnostics}; labels={labels.Count}; mapped={candidates.Count}";
        return DeduplicateCandidates(candidates);
    }

    private static IReadOnlyList<InviteCandidate> DeduplicateCandidates(IEnumerable<InviteCandidate> candidates)
    {
        return candidates
            .GroupBy(candidate => candidate.Id >= 0 ? $"id:{candidate.Id}" : $"name:{candidate.RuntimeName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Id < 0 ? int.MaxValue : candidate.Id)
            .ThenBy(candidate => candidate.RuntimeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static InviteCandidate? BuildCandidate(RuntimeMappedGuestCatalog? catalog, object guest, string source)
    {
        var id = ReadIntMember(guest, "ID", "Id", "id");
        var runtimeName = ReadStringMember(guest, "StringId", "StrID", "Name", "DisplayName", "CharacterName");
        var displayName = ResolveGuestName(catalog, guest, id, runtimeName);
        return new InviteCandidate(guest, id, runtimeName, displayName, source);
    }

    private static IReadOnlyList<string> ReadCurrentDaySceneNpcLabels(out string diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var labels = new List<string>();
        var runtimeDaySceneType = RuntimeReflectionUtility.FindType(RuntimeDaySceneTypeName);
        var dataBaseDayType = RuntimeReflectionUtility.FindType(DataBaseDayTypeName);
        var daySceneMapType = RuntimeReflectionUtility.FindType(DaySceneMapTypeName);
        var sceneManagerType = RuntimeReflectionUtility.FindType(DaySceneSceneManagerTypeName);
        var characterComponentType = RuntimeReflectionUtility.FindType(CharacterConditionComponentTypeName);
        var currentMapLabel = ReadCurrentMapLabel(sceneManagerType);
        TryRefreshCurrentMapNpcs(runtimeDaySceneType);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["mapNpc"] = 0,
            ["mapObject"] = 0,
            ["sceneObject"] = 0,
            ["trackedAnyMap"] = 0,
            ["staticMap"] = 0,
            ["staticUnavailable"] = 0,
        };
        var errors = new List<string>();

        foreach (var label in ReadMapNpcLabels(runtimeDaySceneType, currentMapLabel))
        {
            counts["mapNpc"]++;
            if (seen.Add(label)) labels.Add(label);
        }

        foreach (var label in ReadDaySceneMapCharacterLabels(daySceneMapType))
        {
            counts["mapObject"]++;
            if (seen.Add(label)) labels.Add(label);
        }

        foreach (var label in ReadSceneCharacterLabels(characterComponentType))
        {
            counts["sceneObject"]++;
            if (seen.Add(label)) labels.Add(label);
        }

        foreach (var label in ReadTrackedNpcLabelsFromAllMaps(runtimeDaySceneType, dataBaseDayType, currentMapLabel))
        {
            counts["trackedAnyMap"]++;
            if (seen.Add(label)) labels.Add(label);
        }

        var staticMap = 0;
        var staticUnavailable = 0;
        var keyDiagnostics = "";
        foreach (var label in ReadStaticCurrentMapNpcLabels(runtimeDaySceneType, dataBaseDayType, currentMapLabel, errors, out staticMap, out staticUnavailable, out keyDiagnostics))
        {
            counts["staticMap"]++;
            if (seen.Add(label)) labels.Add(label);
        }

        counts["staticUnavailable"] = staticUnavailable;
        var errorText = errors.Count == 0 ? "" : $"; errors={string.Join("|", errors.Take(4))}";
        diagnostics =
            $"currentMap={currentMapLabel}; mapNpc={counts["mapNpc"]}; mapObject={counts["mapObject"]}; sceneObject={counts["sceneObject"]}; trackedAnyMap={counts["trackedAnyMap"]}; staticMap={staticMap}; staticAccepted={counts["staticMap"]}; staticUnavailable={counts["staticUnavailable"]}; {keyDiagnostics}{errorText}";
        return labels;
    }

    private static void TryRefreshCurrentMapNpcs(Type? runtimeDaySceneType)
    {
        if (runtimeDaySceneType == null) return;
        RuntimeReflectionUtility.InvokeStaticMethod(runtimeDaySceneType, "TryRefreshNPCs");
    }

    private static string ReadCurrentMapLabel(Type? sceneManagerType)
    {
        if (sceneManagerType == null) return "";
        var sceneManager = RuntimeReflectionUtility.GetSingletonInstance(sceneManagerType)
            ?? GetGenericSingletonInstance(sceneManagerType)
            ?? RuntimeReflectionUtility.FindUnityObject(sceneManagerType);
        var current = RuntimeReflectionUtility.GetMemberValue(sceneManager, "CurrentActiveMapLabel")?.ToString()
            ?? RuntimeReflectionUtility.GetMemberValue(sceneManager, "TargetMapLabel")?.ToString();
        return string.IsNullOrWhiteSpace(current) ? "" : current.Trim();
    }

    private static IEnumerable<string> ReadMapNpcLabels(Type? runtimeDaySceneType, string mapLabel)
    {
        if (runtimeDaySceneType == null) yield break;
        if (string.IsNullOrWhiteSpace(mapLabel)) yield break;

        var mapNpcs = InvokeStaticMethodWithSingleParameter(runtimeDaySceneType, "GetMapNPCs", typeof(string), mapLabel);
        foreach (var npcCandidate in RuntimeReflectionUtility.EnumerateObjects(mapNpcs))
        {
            var npc = RuntimeReflectionUtility.NormalizeKeyValueValue(npcCandidate);
            var label = ReadTextMember(npc, "key")
                ?? ReadTextMember(npcCandidate, "Key")
                ?? ReadTextMember(npcCandidate, "key");
            if (!string.IsNullOrWhiteSpace(label)) yield return label;
        }
    }

    private static IEnumerable<string> ReadTrackedNpcLabelsFromAllMaps(Type? runtimeDaySceneType, Type? dataBaseDayType, string currentMapLabel)
    {
        if (runtimeDaySceneType == null || dataBaseDayType == null) yield break;
        if (string.IsNullOrWhiteSpace(currentMapLabel)) yield break;

        var trackedNpcMaps = RuntimeReflectionUtility.GetStaticMemberValue(runtimeDaySceneType, "trackedNPCs");
        foreach (var mapCandidate in RuntimeReflectionUtility.EnumerateObjects(trackedNpcMaps))
        {
            var mapLabel = ReadTextMember(mapCandidate, "Key");
            var npcMap = RuntimeReflectionUtility.NormalizeKeyValueValue(mapCandidate);
            foreach (var npcCandidate in RuntimeReflectionUtility.EnumerateObjects(npcMap))
            {
                var npc = RuntimeReflectionUtility.NormalizeKeyValueValue(npcCandidate);
                var label = ReadTextMember(npc, "key")
                    ?? ReadTextMember(npcCandidate, "Key")
                    ?? ReadTextMember(npcCandidate, "key");
                if (string.IsNullOrWhiteSpace(label)) continue;
                if (!TrackedNpcMatchesCurrentMap(dataBaseDayType, npc, mapLabel, currentMapLabel)) continue;
                if (!IsTrackedNpcAvailable(runtimeDaySceneType, label, out var known) || !known) continue;
                yield return label;
            }
        }
    }

    private static IReadOnlyList<string> ReadStaticCurrentMapNpcLabels(
        Type? runtimeDaySceneType,
        Type? dataBaseDayType,
        string currentMapLabel,
        List<string> errors,
        out int staticMap,
        out int staticUnavailable,
        out string keyDiagnostics)
    {
        staticMap = 0;
        staticUnavailable = 0;
        keyDiagnostics = "npcKeys=0";
        var labels = new List<string>();
        if (dataBaseDayType == null || string.IsNullOrWhiteSpace(currentMapLabel)) return labels;

        var npcKeys = ReadGlobalNpcKeys(dataBaseDayType, errors, out keyDiagnostics);
        foreach (var key in npcKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;

            var npc = InvokeStaticMethodWithSingleParameter(dataBaseDayType, "RefNPC", typeof(string), key);
            if (!StaticNpcMatchesCurrentMap(dataBaseDayType, npc, currentMapLabel)) continue;
            staticMap++;

            if (runtimeDaySceneType != null)
            {
                var available = IsTrackedNpcAvailable(runtimeDaySceneType, key, out var known);
                if (!known || !available)
                {
                    staticUnavailable++;
                    continue;
                }
            }

            labels.Add(key);
        }

        if (staticMap == 0 && npcKeys.Count == 0)
        {
            errors.Add("DataBaseDay NPC key sources empty");
        }

        return labels;
    }

    private static IReadOnlyList<string> ReadGlobalNpcKeys(Type dataBaseDayType, List<string> errors, out string diagnostics)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var counts = new List<string>();

        AddKeysFromSource("GetAllNPCKeys", RuntimeReflectionUtility.InvokeStaticMethod(dataBaseDayType, "GetAllNPCKeys"));
        foreach (var memberName in new[] { "AllMappedNPCsMapping", "AllNPCsMapping", "allNPCs" })
        {
            AddKeysFromDictionary(memberName, RuntimeReflectionUtility.GetStaticMemberValue(dataBaseDayType, memberName));
        }

        diagnostics = $"npcKeys={result.Count}({string.Join(",", counts)})";
        return result.OrderBy(value => value, StringComparer.Ordinal).ToList();

        void AddKeysFromSource(string source, object? value)
        {
            var before = result.Count;
            try
            {
                foreach (var key in ReadTextValues(value))
                {
                    result.Add(key);
                }
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"npc key source {source}: {ex.Message}");
            }

            counts.Add($"{source}:{result.Count - before}");
        }

        void AddKeysFromDictionary(string source, object? dictionary)
        {
            var before = result.Count;
            try
            {
                foreach (var key in ReadTextKeys(dictionary))
                {
                    result.Add(key);
                }

                foreach (var key in ReadNpcKeysFromDictionaryValues(dictionary))
                {
                    result.Add(key);
                }
            }
            catch (Exception ex)
            {
                if (errors.Count < 8) errors.Add($"npc key source {source}: {ex.Message}");
            }

            counts.Add($"{source}:{result.Count - before}");
        }
    }

    private static IEnumerable<string> ReadTextValues(object? value)
    {
        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(value))
        {
            var text = item?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }

        foreach (var item in EnumerateViaEnumerator(value))
        {
            var text = item?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }

    private static IEnumerable<string> ReadTextKeys(object? dictionary)
    {
        foreach (var key in ReadTextValues(RuntimeReflectionUtility.GetMemberValue(dictionary, "Keys")))
        {
            yield return key;
        }

        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(dictionary).Concat(EnumerateViaEnumerator(dictionary)))
        {
            var key = RuntimeReflectionUtility.GetMemberValue(item, "Key")
                ?? RuntimeReflectionUtility.GetMemberValue(item, "key")
                ?? RuntimeReflectionUtility.GetMemberValue(item, "Item1");
            var text = key?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }

    private static IEnumerable<string> ReadNpcKeysFromDictionaryValues(object? dictionary)
    {
        var values = RuntimeReflectionUtility.GetMemberValue(dictionary, "Values");
        foreach (var value in RuntimeReflectionUtility.EnumerateObjects(values).Concat(EnumerateViaEnumerator(values)))
        {
            var key = ReadTextMember(value, "key");
            if (!string.IsNullOrWhiteSpace(key)) yield return key;
        }
    }

    private static IEnumerable<object?> EnumerateViaEnumerator(object? value)
    {
        if (value == null || value is string) yield break;

        var enumerator = RuntimeReflectionUtility.InvokeMethod(value, "GetEnumerator");
        if (enumerator == null) yield break;

        for (var i = 0; i < 1024; i++)
        {
            var hasNext = RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeMethod(enumerator, "MoveNext"));
            if (!hasNext) yield break;
            yield return RuntimeReflectionUtility.GetMemberValue(enumerator, "Current");
        }
    }

    private static IEnumerable<string> ReadDaySceneMapCharacterLabels(Type? daySceneMapType)
    {
        if (daySceneMapType == null) yield break;

        var allCharacters = RuntimeReflectionUtility.GetStaticMemberValue(daySceneMapType, "allCharacters");
        foreach (var characterCandidate in RuntimeReflectionUtility.EnumerateObjects(allCharacters))
        {
            var component = RuntimeReflectionUtility.NormalizeKeyValueValue(characterCandidate);
            var label = ReadTextMember(characterCandidate, "Key")
                ?? ReadTextMember(component, "CharacterLabel");
            var trackedNpcData = RuntimeReflectionUtility.GetMemberValue(component, "trackedNPCData");
            label ??= ReadTextMember(trackedNpcData, "key");
            if (!string.IsNullOrWhiteSpace(label)) yield return label;
        }
    }

    private static IEnumerable<string> ReadSceneCharacterLabels(Type? characterComponentType)
    {
        if (characterComponentType == null) yield break;

        foreach (var component in RuntimeReflectionUtility.FindUnityObjectsIncludingInactive(characterComponentType))
        {
            var label = ReadTextMember(component, "CharacterLabel");
            var trackedNpcData = RuntimeReflectionUtility.GetMemberValue(component, "trackedNPCData");
            label ??= ReadTextMember(trackedNpcData, "key");
            if (!string.IsNullOrWhiteSpace(label)) yield return label;
        }
    }

    private static bool TrackedNpcMatchesCurrentMap(Type dataBaseDayType, object? npc, string? mapLabel, string currentMapLabel)
    {
        foreach (var candidate in ResolveTrackedNpcMapLabels(dataBaseDayType, npc, mapLabel))
        {
            if (MapLabelsEqual(candidate, currentMapLabel)) return true;
        }

        return false;
    }

    private static IEnumerable<string> ResolveTrackedNpcMapLabels(Type dataBaseDayType, object? npc, string? mapLabel)
    {
        if (!string.IsNullOrWhiteSpace(mapLabel)) yield return mapLabel.Trim();

        var overridePosition = RuntimeReflectionUtility.GetMemberValue(npc, "overridePosition");
        var overrideMapLabel = ReadTextMember(overridePosition, "mapLabel");
        if (!string.IsNullOrWhiteSpace(overrideMapLabel)) yield return overrideMapLabel;

        var currentDestination = RuntimeReflectionUtility.GetMemberValue(npc, "currentDestination");
        foreach (var label in ResolveDestinationMapLabels(dataBaseDayType, currentDestination))
        {
            yield return label;
        }
    }

    private static bool StaticNpcMatchesCurrentMap(Type dataBaseDayType, object? npc, string currentMapLabel)
    {
        foreach (var destination in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(npc, "possibleDestinations")))
        {
            foreach (var label in ResolveDestinationMapLabels(dataBaseDayType, destination))
            {
                if (MapLabelsEqual(label, currentMapLabel)) return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ResolveDestinationMapLabels(Type dataBaseDayType, object? destination)
    {
        var spawnMarker = ReadTextMember(destination, "spawnMarker");
        if (string.IsNullOrWhiteSpace(spawnMarker)) yield break;

        var mapLabel = InvokeStaticMethodWithSingleParameter(dataBaseDayType, "GetMapLabelFromSpawnMarker", typeof(string), spawnMarker)?.ToString();
        if (!string.IsNullOrWhiteSpace(mapLabel)) yield return mapLabel.Trim();
    }

    private static bool IsTrackedNpcAvailable(Type runtimeDaySceneType, string key, out bool known)
    {
        known = false;
        var value = InvokeStaticMethodWithSingleParameter(runtimeDaySceneType, "RefTrackedNPCAvailability", typeof(string), key);
        if (value == null) return false;

        known = true;
        return RuntimeReflectionUtility.ToBool(value);
    }

    private static bool MapLabelsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasInviteDialog(object guest, int level, bool succeed)
    {
        var dialogs = RuntimeReflectionUtility.InvokeMethod(guest, "GetInviteDialogPackageAtKizunaLevel", level, succeed);
        return RuntimeReflectionUtility.CountObjects(dialogs) > 0;
    }

    private static bool HasNpcInvited(object statusTracker, int id)
    {
        return RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeMethod(statusTracker, "HasNPCInvited", id));
    }

    private static object? InvokeStaticMethodWithSingleParameter(
        Type type,
        string methodName,
        Type parameterType,
        object? arg)
    {
        var method = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)) return false;
                var parameters = candidate.GetParameters();
                return parameters.Length == 1 && ParameterTypeMatches(parameters[0].ParameterType, parameterType);
            });
        if (method == null) return null;

        try
        {
            return method.Invoke(null, new[] { arg });
        }
        catch
        {
            return null;
        }
    }

    private static bool ParameterTypeMatches(Type actual, Type expected)
    {
        if (actual == expected) return true;
        if (string.Equals(actual.FullName, expected.FullName, StringComparison.Ordinal)) return true;

        return expected == typeof(int)
            ? string.Equals(actual.FullName, "System.Int32", StringComparison.Ordinal)
            : expected == typeof(string) && string.Equals(actual.FullName, "System.String", StringComparison.Ordinal);
    }

    private static object? GetGenericSingletonInstance(Type concreteType)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? singletonType;
            try
            {
                singletonType = assembly.GetType("DEYU.Singletons.Singleton`1", throwOnError: false);
            }
            catch
            {
                continue;
            }

            if (singletonType == null) continue;

            try
            {
                var closedType = singletonType.MakeGenericType(concreteType);
                var instance = RuntimeReflectionUtility.GetStaticMemberValue(closedType, "Instance")
                    ?? RuntimeReflectionUtility.InvokeStaticMethod(closedType, "get_Instance");
                if (instance != null) return instance;
            }
            catch
            {
                // Try the next loaded assembly.
            }
        }

        return null;
    }

    private static void AddSkipped(RareGuestInvitationResult result, InviteCandidate candidate, string reason)
    {
        result.Skipped.Add(new RareGuestInvitationEntry
        {
            Id = candidate.Id,
            Name = candidate.DisplayName,
            RuntimeName = candidate.RuntimeName,
            Reason = reason,
        });
    }

    private static string BuildStatus(RareGuestInvitationResult result, string source)
    {
        var sourceLabel = source == "current-day-scene" ? "当前日间场景" : "运行时稀客数据";
        if (result.Invited.Count > 0)
        {
            return $"{sourceLabel}已邀请 {result.Invited.Count} 位稀客。";
        }

        if (result.UsableCount > 0)
        {
            return $"{sourceLabel}没有新的可写入邀请，可能候选已被忽略或原生记录失败。";
        }

        if (result.ExistingControlledCount > 0)
        {
            return $"{sourceLabel}中的可邀请稀客今晚均已邀请。";
        }

        return $"{sourceLabel}没有新的可邀请稀客。";
    }

    private static string BuildListStatus(RareGuestInvitationResult result, string source)
    {
        var sourceLabel = source == "current-day-scene" ? "当前日间场景" : "运行时稀客数据";
        if (result.Available.Count > 0)
        {
            return $"{sourceLabel}有 {result.Available.Count} 位可邀请稀客。";
        }

        if (result.ExistingControlledCount > 0)
        {
            return $"{sourceLabel}中的可邀请稀客今晚均已邀请。";
        }

        return $"{sourceLabel}没有新的可邀请稀客。";
    }

    private static string ResolveGuestName(RuntimeMappedGuestCatalog? catalog, object guest, int id, string runtimeName)
    {
        var resolved = catalog?.Resolve(id >= 0 ? id : null, runtimeName);
        if (!string.IsNullOrWhiteSpace(resolved?.Name)) return resolved.Name;

        var text = RuntimeReflectionUtility.GetMemberValue(guest, "Text")
            ?? RuntimeReflectionUtility.InvokeMethod(guest, "get_Text");
        var textName = RuntimeReflectionUtility.GetMemberValue(text, "Name")?.ToString()
            ?? RuntimeReflectionUtility.InvokeMethod(text, "get_Name")?.ToString();
        if (!string.IsNullOrWhiteSpace(textName)) return textName.Trim();

        var memberName = ReadStringMember(guest, "Name", "DisplayName", "CharacterName");
        if (!string.IsNullOrWhiteSpace(memberName)) return memberName;

        return string.IsNullOrWhiteSpace(runtimeName) ? $"#{id}" : runtimeName;
    }

    private static int ReadIntMember(object? instance, params string[] names)
    {
        foreach (var name in names)
        {
            var value = RuntimeReflectionUtility.GetMemberValue(instance, name);
            if (value != null) return RuntimeReflectionUtility.ToInt(value, -1);
        }

        return -1;
    }

    private static string ReadStringMember(object? instance, params string[] names)
    {
        foreach (var name in names)
        {
            var value = RuntimeReflectionUtility.GetMemberValue(instance, name)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return "";
    }

    private static string? ReadTextMember(object? instance, string name)
    {
        var value = RuntimeReflectionUtility.GetMemberValue(instance, name)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record InviteCandidate(object Guest, int Id, string RuntimeName, string DisplayName, string Source);

    private sealed class InvitationContext
    {
        public bool Ok { get; init; }
        public RareGuestInvitationResult Result { get; init; } = new();
        public Type? AlbumType { get; init; }
        public object? StatusTracker { get; init; }
        public IReadOnlyList<InviteCandidate> Candidates { get; init; } = Array.Empty<InviteCandidate>();
        public string Source { get; init; } = "";
        public string Diagnostics { get; init; } = "";

        public static InvitationContext Failed(RareGuestInvitationResult result)
        {
            return new InvitationContext
            {
                Ok = false,
                Result = result,
            };
        }
    }

}
