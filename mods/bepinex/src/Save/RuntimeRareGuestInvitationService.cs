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
    private const string DaySceneChatSelectionPanelTypeName = "DayScene.UI.DaySceneChatSelectionPannel";
    private const string RunTimeAlbumTypeName = "GameData.RunTime.Common.RunTimeAlbum";
    private const string StatusTrackerTypeName = "GameData.RunTime.Common.StatusTracker";
    private const string RuntimeDaySceneTypeName = "GameData.RunTime.DaySceneUtility.RunTimeDayScene";
    private const string DaySceneMapTypeName = "DayScene.DaySceneMap";
    private const string CharacterConditionComponentTypeName = "DayScene.Interactables.Collections.ConditionComponents.CharacterConditionComponent";

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

    private static RareGuestInvitationResult InviteAllAvailableCore(DataRepository? repository, ManualLogSource? log)
    {
        var dataBaseCharacterType = RuntimeReflectionUtility.FindType(DataBaseCharacterTypeName);
        var panelType = RuntimeReflectionUtility.FindType(DaySceneChatSelectionPanelTypeName);
        var albumType = RuntimeReflectionUtility.FindType(RunTimeAlbumTypeName);
        var statusTrackerType = RuntimeReflectionUtility.FindType(StatusTrackerTypeName);
        if (dataBaseCharacterType == null || panelType == null || albumType == null || statusTrackerType == null)
        {
            return Fail("游戏原生羁绊邀请系统尚未初始化。请在读取存档后的日间场景再试。");
        }

        var statusTracker = RuntimeReflectionUtility.GetSingletonInstance(statusTrackerType)
            ?? GetGenericSingletonInstance(statusTrackerType)
            ?? RuntimeReflectionUtility.FindUnityObject(statusTrackerType);
        if (statusTracker == null)
        {
            return Fail("未读取到游戏邀请状态。请在读取存档后的日间场景再试。");
        }

        var catalog = repository == null ? null : new RuntimeMappedGuestCatalog(repository);
        var candidates = ReadInviteCandidates(dataBaseCharacterType, catalog, out var source);
        if (candidates.Count == 0)
        {
            return Fail("未读取到稀客邀请候选。请确认已进入存档后的日间场景。");
        }

        var result = new RareGuestInvitationResult
        {
            RuntimeAvailable = true,
            CandidateCount = candidates.Count,
            ExistingSlotCount = RuntimeReflectionUtility.CountObjects(RuntimeReflectionUtility.GetMemberValue(statusTracker, "InvitedGuests")),
        };

        foreach (var candidate in candidates)
        {
            ProcessCandidate(result, panelType, albumType, statusTracker, candidate);
        }

        result.Ok = true;
        result.InvitedCount = result.Invited.Count;
        result.SkippedCount = result.Skipped.Count;
        result.Status = BuildStatus(result, source);
        log?.LogInfo($"Invite all rare guests: {result.Status} source={source}, candidates={result.CandidateCount}, eligible={result.UsableCount}, existingInvited={result.ExistingControlledCount}, invited={result.InvitedCount}, skipped={result.SkippedCount}");
        return result;
    }

    private static void ProcessCandidate(
        RareGuestInvitationResult result,
        Type panelType,
        Type albumType,
        object statusTracker,
        InviteCandidate candidate)
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

        if (!string.IsNullOrWhiteSpace(candidate.RuntimeName) && HasTemptInvited(statusTracker, candidate.RuntimeName))
        {
            AddSkipped(result, candidate, "今日已尝试邀请");
            return;
        }

        var level = RuntimeReflectionUtility.ToInt(
            RuntimeReflectionUtility.InvokeStaticMethod(albumType, "GetOrGenerateSpecialNPCKizunaLevel", candidate.Id),
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

        if (level < 5 && !HasInviteDialog(candidate.Guest, level, succeed: false))
        {
            AddSkipped(result, candidate, $"当前羁绊等级无失败邀请对话 {level}");
            return;
        }

        result.UsableCount++;
        var inviteCheck = TryInviteSpecGuest(panelType, candidate.Guest, level);
        if (!inviteCheck.Invoked)
        {
            AddSkipped(result, candidate, inviteCheck.Error ?? "原生邀请判定调用失败");
            return;
        }

        if (!inviteCheck.Succeeded)
        {
            AddSkipped(result, candidate, $"原生邀请判定失败（羁绊 {level}）");
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
            Reason = $"已按原生羁绊邀请记录（羁绊 {level}）",
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
        out string source)
    {
        var labelCandidates = ReadCurrentDaySceneNpcLabels()
            .Select(label => RuntimeReflectionUtility.InvokeStaticMethod(dataBaseCharacterType, "RefSGuest", label))
            .Where(guest => guest != null)
            .Select(guest => BuildCandidate(catalog, guest!, "current-day-scene"))
            .Where(candidate => candidate != null)
            .Select(candidate => candidate!)
            .ToList();
        if (labelCandidates.Count > 0)
        {
            source = "current-day-scene";
            return DeduplicateCandidates(labelCandidates);
        }

        var runtimeGuests = RuntimeReflectionUtility.InvokeStaticMethod(dataBaseCharacterType, "GetSpecialGuestsAndMappedGuests");
        var candidates = RuntimeReflectionUtility
            .EnumerateObjects(runtimeGuests)
            .Where(guest => guest != null)
            .Select(guest => BuildCandidate(catalog, guest!, "runtime-all"))
            .Where(candidate => candidate != null)
            .Select(candidate => candidate!)
            .ToList();
        source = "runtime-all";
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

    private static IEnumerable<string> ReadCurrentDaySceneNpcLabels()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var runtimeDaySceneType = RuntimeReflectionUtility.FindType(RuntimeDaySceneTypeName);
        var daySceneMapType = RuntimeReflectionUtility.FindType(DaySceneMapTypeName);
        var characterComponentType = RuntimeReflectionUtility.FindType(CharacterConditionComponentTypeName);

        foreach (var label in ReadTrackedNpcLabels(runtimeDaySceneType))
        {
            if (seen.Add(label)) yield return label;
        }

        foreach (var label in ReadDaySceneMapCharacterLabels(daySceneMapType))
        {
            if (seen.Add(label)) yield return label;
        }

        foreach (var label in ReadSceneCharacterLabels(characterComponentType))
        {
            if (seen.Add(label)) yield return label;
        }
    }

    private static IEnumerable<string> ReadTrackedNpcLabels(Type? runtimeDaySceneType)
    {
        if (runtimeDaySceneType == null) yield break;

        var trackedNpcMaps = RuntimeReflectionUtility.GetStaticMemberValue(runtimeDaySceneType, "trackedNPCs");
        foreach (var mapCandidate in RuntimeReflectionUtility.EnumerateObjects(trackedNpcMaps))
        {
            var map = RuntimeReflectionUtility.NormalizeKeyValueValue(mapCandidate);
            foreach (var npcCandidate in RuntimeReflectionUtility.EnumerateObjects(map))
            {
                var npc = RuntimeReflectionUtility.NormalizeKeyValueValue(npcCandidate);
                var label = ReadTextMember(npc, "key")
                    ?? ReadTextMember(npcCandidate, "Key")
                    ?? ReadTextMember(npcCandidate, "key");
                if (!string.IsNullOrWhiteSpace(label)) yield return label;
            }
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

    private static bool HasInviteDialog(object guest, int level, bool succeed)
    {
        var dialogs = RuntimeReflectionUtility.InvokeMethod(guest, "GetInviteDialogPackageAtKizunaLevel", level, succeed);
        return RuntimeReflectionUtility.CountObjects(dialogs) > 0;
    }

    private static InviteSpecGuestResult TryInviteSpecGuest(Type panelType, object guest, int level)
    {
        var method = panelType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "InviteSpecGuest", StringComparison.Ordinal)
                && candidate.GetParameters().Length == 3);
        if (method == null)
        {
            return new InviteSpecGuestResult(false, false, "未找到原生 InviteSpecGuest 方法");
        }

        var args = new object?[] { guest, level, null };
        try
        {
            var result = method.Invoke(null, args);
            return new InviteSpecGuestResult(true, RuntimeReflectionUtility.ToBool(result), null);
        }
        catch (TargetInvocationException ex)
        {
            return new InviteSpecGuestResult(false, false, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return new InviteSpecGuestResult(false, false, ex.Message);
        }
    }

    private static bool HasNpcInvited(object statusTracker, int id)
    {
        return RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeMethod(statusTracker, "HasNPCInvited", id));
    }

    private static bool HasTemptInvited(object statusTracker, string runtimeName)
    {
        return RuntimeReflectionUtility.ToBool(RuntimeReflectionUtility.InvokeMethod(statusTracker, "HasTemptInvited", runtimeName));
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
            return $"{sourceLabel}没有新的成功邀请，可能本次原生成功率判定未通过。";
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

    private sealed record InviteSpecGuestResult(bool Invoked, bool Succeeded, string? Error);
}
