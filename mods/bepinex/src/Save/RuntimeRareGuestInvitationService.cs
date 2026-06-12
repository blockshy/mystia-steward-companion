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
    private const string SpecialGuestControlledTypeName = "Story.SpecialGuestControlled";
    private const string ControlledSpecialGuestTypeName = "Story.ControlledSpecialGuest";
    private const string ControlStatusTypeName = "Story.ControlStatus";
    private const string RunTimePlayerDataTypeName = "GameData.RunTime.Common.RunTimePlayerData";

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
        var controlledType = RuntimeReflectionUtility.FindType(SpecialGuestControlledTypeName);
        if (controlledType == null)
        {
            return Fail("未找到游戏原生稀客邀请系统。");
        }

        var controlled = RuntimeReflectionUtility.GetSingletonInstance(controlledType)
            ?? GetGenericSingletonInstance(controlledType)
            ?? RuntimeReflectionUtility.FindUnityObject(controlledType);
        if (controlled == null)
        {
            return Fail("游戏原生稀客邀请系统尚未初始化。请在读取存档后的日间或经营准备阶段再试。");
        }

        var playerDataType = RuntimeReflectionUtility.FindType(RunTimePlayerDataTypeName);
        if (playerDataType != null)
        {
            var hasDlc5 = RuntimeReflectionUtility.GetStaticMemberValue(playerDataType, "HasDLC5")
                ?? RuntimeReflectionUtility.InvokeStaticMethod(playerDataType, "get_HasDLC5");
            if (hasDlc5 != null && !RuntimeReflectionUtility.ToBool(hasDlc5))
            {
                return Fail("当前存档未启用游戏原生稀客邀请系统。");
            }
        }

        var candidateGuests = ReadCandidateGuests(controlled).OfType<object>().ToList();
        if (candidateGuests.Count == 0)
        {
            return Fail("未读取到可邀请稀客候选。");
        }

        var controlledList = RuntimeReflectionUtility.GetMemberValue(controlled, "m_ControlledSpecialGuests")
            ?? RuntimeReflectionUtility.GetMemberValue(controlled, "ControlledSpecialGuests")
            ?? (playerDataType == null ? null : RuntimeReflectionUtility.InvokeStaticMethod(playerDataType, "GetControlledSpecialGuests"));
        if (controlledList == null)
        {
            return Fail("未读取到游戏受控稀客列表。");
        }

        var controlledEntryType = RuntimeReflectionUtility.FindType(ControlledSpecialGuestTypeName);
        var controlStatusType = RuntimeReflectionUtility.FindType(ControlStatusTypeName);
        if (controlledEntryType == null || controlStatusType == null)
        {
            return Fail("未找到游戏受控稀客数据结构。");
        }

        var currentEntries = ReadControlledEntries(controlledList)
            .Where(entry => entry.Status != 0)
            .GroupBy(entry => entry.Id)
            .Select(group => group.OrderByDescending(entry => entry.Status).First())
            .ToList();
        var alreadyControlled = currentEntries.Select(entry => entry.Id).ToHashSet();
        var catalog = repository == null ? null : new RuntimeMappedGuestCatalog(repository);
        var result = new RareGuestInvitationResult
        {
            RuntimeAvailable = true,
            CandidateCount = candidateGuests.Count,
            ExistingSlotCount = RuntimeReflectionUtility.CountObjects(controlledList),
            ExistingControlledCount = currentEntries.Count,
        };

        var nextEntries = new List<object>();

        foreach (var guest in candidateGuests)
        {
            var id = ReadIntMember(guest, "id", "Id", "ID");
            var runtimeName = ReadStringMember(guest, "StringId", "StrID", "Name", "DisplayName", "CharacterName");
            var displayName = ResolveGuestName(catalog, guest, id, runtimeName);
            if (id < 0)
            {
                result.Skipped.Add(new RareGuestInvitationEntry
                {
                    Id = id,
                    Name = displayName,
                    RuntimeName = runtimeName,
                    Reason = "未读取到稀客 ID",
                });
                continue;
            }

            if (alreadyControlled.Contains(id))
            {
                result.Skipped.Add(new RareGuestInvitationEntry
                {
                    Id = id,
                    Name = displayName,
                    RuntimeName = runtimeName,
                    Reason = "已在邀请或受控列表中",
                });
                continue;
            }

            if (!IsUsable(controlled, id))
            {
                result.Skipped.Add(new RareGuestInvitationEntry
                {
                    Id = id,
                    Name = displayName,
                    RuntimeName = runtimeName,
                    Reason = "游戏判定当前不可邀请",
                });
                continue;
            }

            result.UsableCount++;
            var nextEntry = CreateControlledEntry(controlledEntryType, controlStatusType, id, 1);
            if (nextEntry == null)
            {
                result.Skipped.Add(new RareGuestInvitationEntry
                {
                    Id = id,
                    Name = displayName,
                    RuntimeName = runtimeName,
                    Reason = "无法创建受控稀客条目",
                });
                continue;
            }

            alreadyControlled.Add(id);
            nextEntries.Add(nextEntry);
            result.Invited.Add(new RareGuestInvitationEntry
            {
                Id = id,
                Name = displayName,
                RuntimeName = runtimeName,
                Reason = "已写入游戏原生邀请队列",
            });
        }

        if (result.Invited.Count > 0 && !AppendControlledEntries(controlledList, nextEntries))
        {
            return Fail("写入游戏受控稀客列表失败。");
        }

        if (result.Invited.Count > 0 && playerDataType != null)
        {
            var persisted = RuntimeReflectionUtility.InvokeStaticMethod(playerDataType, "SetControlledSpecialGuests", controlledList);
            _ = persisted;
        }

        if (result.Invited.Count == 0)
        {
            result.ScheduledSlotCount = ScheduleNativeInvitationSlots(controlled, controlledList, candidateGuests.Count);
            result.ExistingSlotCount = Math.Max(result.ExistingSlotCount, RuntimeReflectionUtility.CountObjects(controlledList) - result.ScheduledSlotCount);
        }

        result.Ok = true;
        result.InvitedCount = result.Invited.Count + result.ScheduledSlotCount;
        result.SkippedCount = result.Skipped.Count;
        result.Status = BuildStatus(result);
        log?.LogInfo($"Invite all rare guests: {result.Status} candidates={result.CandidateCount}, usable={result.UsableCount}, existingSlots={result.ExistingSlotCount}, existingControlled={result.ExistingControlledCount}, scheduledSlots={result.ScheduledSlotCount}, skipped={result.SkippedCount}");
        return result;
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

    private static IEnumerable<object?> ReadCandidateGuests(object controlled)
    {
        var cache = RuntimeReflectionUtility.GetMemberValue(controlled, "m_SpecialGuestsCache")
            ?? RuntimeReflectionUtility.GetMemberValue(controlled, "SpecialGuestsCache");
        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(cache))
        {
            if (item != null) yield return item;
        }
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

    private static IReadOnlyList<ControlledEntry> ReadControlledEntries(object controlledList)
    {
        var entries = new List<ControlledEntry>();
        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(controlledList))
        {
            if (item == null) continue;
            entries.Add(new ControlledEntry(
                ReadIntMember(item, "Id", "ID", "id"),
                ReadIntMember(item, "Status", "status")));
        }

        return entries;
    }

    private static bool IsUsable(object controlled, int id)
    {
        var result = RuntimeReflectionUtility.InvokeMethod(controlled, "CheckSpecialGuestUsability", id);
        return RuntimeReflectionUtility.ToBool(result);
    }

    private static object? CreateControlledEntry(Type controlledEntryType, Type controlStatusType, int id, int status)
    {
        try
        {
            var statusValue = controlStatusType.IsEnum
                ? Enum.ToObject(controlStatusType, status)
                : Convert.ChangeType(status, controlStatusType);
            var created = Activator.CreateInstance(controlledEntryType, id, statusValue);
            if (created != null) return created;
        }
        catch
        {
            // Fall back to field assignment below.
        }

        try
        {
            var created = Activator.CreateInstance(controlledEntryType);
            if (created == null) return null;
            SetMemberValue(created, "Id", id);
            SetMemberValue(created, "Status", status);
            return created;
        }
        catch
        {
            return null;
        }
    }

    private static bool AppendControlledEntries(object controlledList, IReadOnlyList<object> entries)
    {
        foreach (var entry in entries)
        {
            if (!InvokeInstanceMethod(controlledList, "Add", entry)) return false;
        }

        return true;
    }

    private static int ScheduleNativeInvitationSlots(object controlled, object controlledList, int candidateCount)
    {
        var scheduled = 0;
        var previousCount = RuntimeReflectionUtility.CountObjects(controlledList);
        var targetCount = Math.Max(previousCount, candidateCount);

        while (previousCount < targetCount)
        {
            RuntimeReflectionUtility.InvokeMethod(controlled, "ControlScheduled");
            var nextCount = RuntimeReflectionUtility.CountObjects(controlledList);
            if (nextCount <= previousCount) break;

            scheduled += nextCount - previousCount;
            previousCount = nextCount;
        }

        return scheduled;
    }

    private static string BuildStatus(RareGuestInvitationResult result)
    {
        if (result.Invited.Count > 0)
        {
            return $"已邀请 {result.Invited.Count} 位稀客。";
        }

        if (result.ScheduledSlotCount > 0)
        {
            return $"已新增 {result.ScheduledSlotCount} 个原生邀请名额；游戏会在经营准备时自动选择可邀请稀客。";
        }

        if (result.ExistingSlotCount > 0)
        {
            return $"已有 {result.ExistingSlotCount} 个原生邀请名额或受控稀客，无需新增。";
        }

        return "没有新的可邀请稀客或可用邀请名额。";
    }

    private static bool InvokeInstanceMethod(object instance, string name, params object?[] args)
    {
        var method = FindMethod(instance.GetType(), name, args.Length);
        if (method == null) return false;

        try
        {
            method.Invoke(instance, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo? FindMethod(Type type, string name, int argCount)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return type.GetMethods(flags).FirstOrDefault(method =>
            string.Equals(method.Name, name, StringComparison.Ordinal)
            && method.GetParameters().Length == argCount);
    }

    private static void SetMemberValue(object instance, string name, object value)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = instance.GetType();
        var field = type.GetField(name, flags)
            ?? type.GetField($"<{name}>k__BackingField", flags)
            ?? type.GetField($"m_{name}", flags)
            ?? type.GetField($"_{name}", flags);
        if (field != null)
        {
            field.SetValue(instance, CoerceValue(value, field.FieldType));
            return;
        }

        var property = type.GetProperty(name, flags);
        property?.SetValue(instance, CoerceValue(value, property.PropertyType));
    }

    private static object CoerceValue(object value, Type targetType)
    {
        if (targetType.IsEnum) return Enum.ToObject(targetType, value);
        return targetType == typeof(byte) ? Convert.ToByte(value) : Convert.ChangeType(value, targetType);
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

    private sealed record ControlledEntry(int Id, int Status);
}
