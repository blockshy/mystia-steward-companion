using System.Reflection;
using System.Runtime.ExceptionServices;
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
    public string KizunaLevelFilter { get; set; } = "";
    public string Source { get; set; } = "";
    public string Diagnostics { get; set; } = "";
    public string Scope { get; set; } = "";
    public string CurrentMapLabel { get; set; } = "";
    public string CurrentMapName { get; set; } = "";
    public List<RareGuestInvitationEntry> Candidates { get; set; } = new();
    public List<RareGuestInvitationEntry> Available { get; set; } = new();
    public List<RareGuestInvitationEntry> ExistingInvited { get; set; } = new();
    public List<RareGuestInvitationEntry> Invited { get; set; } = new();
    public List<RareGuestInvitationEntry> Skipped { get; set; } = new();
}

internal sealed class RareGuestInvitationEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string RuntimeName { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "";
    public bool CanInvite { get; set; }
    public bool IsCurrentScene { get; set; }
    public int KizunaLevel { get; set; } = -1;
    public List<string> SceneLabels { get; set; } = new();
    public List<string> SceneNames { get; set; } = new();
}

internal static class RuntimeRareGuestInvitationService
{
    private const string DataBaseCharacterTypeName = "GameData.Core.Collections.CharacterUtility.DataBaseCharacter";
    private const string DataBaseDayTypeName = "GameData.Core.Collections.DaySceneUtility.DataBaseDay";
    private const string DaySceneLanguageTypeName = "GameData.CoreLanguage.Collections.DaySceneLanguage";
    private const string NpcTypeName = "GameData.Core.Collections.DaySceneUtility.Collections.NPC";
    private const string RunTimeAlbumTypeName = "GameData.RunTime.Common.RunTimeAlbum";
    private const string SpecialGuestRunTimeDataTypeName =
        "GameData.RunTime.Common.RunTimeAlbum+SpecialGuestRunTimeData";
    private const string RunTimePlayerDataTypeName = "GameData.RunTime.Common.RunTimePlayerData";
    private const string StatusTrackerTypeName = "GameData.RunTime.Common.StatusTracker";
    private const string RuntimeDaySceneTypeName = "GameData.RunTime.DaySceneUtility.RunTimeDayScene";
    private const string TrackedNpcTypeName = "GameData.RunTime.DaySceneUtility.Collection.TrackedNPC";
    private const string DaySceneSceneManagerTypeName = "DayScene.SceneManager";
    private const string SingletonTypeDefinitionName = "DEYU.Singletons.Singleton`1";
    private const string MonoSingletonTypeDefinitionName = "DEYU.Singletons.MonoSingleton`1";
    private const string Il2CppDictionaryTypeName = "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const int MaxInvitationGuestCount = 4096;

    public static RareGuestInvitationResult ListAvailable(
        DataRepository? repository,
        ManualLogSource? log,
        string scopeText = "",
        string kizunaLevelsText = "")
    {
        var loggedScope = scopeText.Trim();
        try
        {
            var scope = ParseScope(scopeText);
            loggedScope = ScopeToText(scope);
            return ListAvailableCore(
                repository,
                log,
                scope,
                ParseKizunaLevelFilter(kizunaLevelsText));
        }
        catch (Exception ex)
        {
            log?.LogError(
                $"List inviteable rare guests failed: scope={loggedScope}, "
                + $"stage=read-context-or-candidates, error={ex}");
            return new RareGuestInvitationResult
            {
                Ok = false,
                RuntimeAvailable = false,
                Status = "读取可邀请稀客失败。",
                Error = ex.Message,
                Scope = loggedScope,
            };
        }
    }

    public static RareGuestInvitationResult InviteOne(
        DataRepository? repository,
        int guestId,
        ManualLogSource? log,
        string scopeText,
        RareGuestInvitationWriteExpectation writeExpectation)
    {
        try
        {
            return InviteOneCore(
                repository,
                guestId,
                log,
                ParseScope(scopeText),
                writeExpectation);
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

    public static RareGuestInvitationResult InviteAllAvailable(
        DataRepository? repository,
        ManualLogSource? log,
        string scopeText,
        string kizunaLevelsText,
        RareGuestInvitationWriteExpectation writeExpectation)
    {
        try
        {
            return InviteAllAvailableCore(
                repository,
                log,
                ParseScope(scopeText),
                ParseKizunaLevelFilter(kizunaLevelsText),
                writeExpectation);
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

    public static DaySceneMapInfo ReadCurrentDaySceneMapInfo()
    {
        if (!RuntimeSceneReadinessCapture.CanReadDaySceneRuntime())
        {
            return new DaySceneMapInfo();
        }

        var sceneManagerType = RuntimeReflectionUtility.FindType(DaySceneSceneManagerTypeName);
        var label = ReadCurrentMapLabel(sceneManagerType);
        return new DaySceneMapInfo
        {
            Label = label,
            Name = NormalizePlaceName(label),
        };
    }

    private static RareGuestInvitationResult ListAvailableCore(
        DataRepository? repository,
        ManualLogSource? log,
        RareGuestInvitationScope scope,
        KizunaLevelFilter kizunaFilter)
    {
        var context = ReadInvitationContext(repository, scope);
        if (!context.Ok)
        {
            log?.LogWarning(
                $"List inviteable rare guests unavailable: scope={ScopeToText(scope)}, "
                + $"stage=read-context, status={context.Result.Status}, "
                + $"error={context.Result.Error ?? ""}, diagnostics={context.Result.Diagnostics}");
            return context.Result;
        }

        var result = CreateBaseResult(context);
        foreach (var candidate in context.Candidates)
        {
            ProcessCandidate(result, context.StatusTracker!, candidate, writeInvitation: false, kizunaFilter);
        }

        result.Ok = true;
        result.InvitedCount = result.Invited.Count;
        result.SkippedCount = result.Skipped.Count;
        result.Status = BuildListStatus(result, context.Source);
        log?.LogInfo(
            $"List inviteable rare guests: {result.Status} scope={context.ScopeText}, "
            + $"kizuna={kizunaFilter.Text}, source={context.Source}, diagnostics={context.Diagnostics}, "
            + $"candidates={result.CandidateCount}, available={result.Available.Count}, skipped={result.SkippedCount}");
        return result;
    }

    private static RareGuestInvitationResult InviteOneCore(
        DataRepository? repository,
        int guestId,
        ManualLogSource? log,
        RareGuestInvitationScope scope,
        RareGuestInvitationWriteExpectation writeExpectation)
    {
        if (guestId < 0) return Fail("稀客 ID 无效。");

        var context = ReadInvitationContext(repository, scope);
        if (!context.Ok) return context.Result;

        var result = CreateBaseResult(context);
        var target = context.Candidates.FirstOrDefault(
            candidate => candidate.CanonicalGuestId == guestId);
        if (target == null)
        {
            result.Ok = false;
            result.RuntimeAvailable = true;
            result.Status = "当前范围未找到该稀客。";
            result.Error = result.Status;
            return result;
        }

        ProcessCandidate(
            result,
            context.StatusTracker!,
            target,
            writeInvitation: true,
            writeExpectation: writeExpectation);
        foreach (var candidate in context.Candidates.Where(
                     candidate => candidate.CanonicalGuestId != guestId))
        {
            ProcessCandidate(result, context.StatusTracker!, candidate, writeInvitation: false);
        }

        result.InvitedCount = result.Invited.Count;
        result.SkippedCount = result.Skipped.Count;
        var targetEntry = result.Candidates.FirstOrDefault(entry => entry.Id == guestId);
        result.Ok = result.Invited.Count > 0
            || string.Equals(targetEntry?.Status, "invited", StringComparison.Ordinal);
        result.Status = result.Invited.Count > 0
            ? $"已邀请 {result.Invited[0].Name}。"
            : BuildStatus(result, context.Source);
        if (!result.Ok) result.Error = targetEntry?.Reason ?? result.Status;

        log?.LogInfo(
            $"Invite rare guest {guestId}: {result.Status} source={context.Source}, "
            + $"diagnostics={context.Diagnostics}, available={result.Available.Count}, "
            + $"invited={result.InvitedCount}, skipped={result.SkippedCount}");
        return result;
    }

    private static RareGuestInvitationResult InviteAllAvailableCore(
        DataRepository? repository,
        ManualLogSource? log,
        RareGuestInvitationScope scope,
        KizunaLevelFilter kizunaFilter,
        RareGuestInvitationWriteExpectation writeExpectation)
    {
        var context = ReadInvitationContext(repository, scope);
        if (!context.Ok) return context.Result;

        var result = CreateBaseResult(context);
        foreach (var candidate in context.Candidates)
        {
            ProcessCandidate(
                result,
                context.StatusTracker!,
                candidate,
                writeInvitation: true,
                kizunaFilter,
                writeExpectation);
        }

        result.Ok = true;
        result.InvitedCount = result.Invited.Count;
        result.SkippedCount = result.Skipped.Count;
        result.Status = BuildStatus(result, context.Source);
        log?.LogInfo(
            $"Invite all rare guests: {result.Status} scope={context.ScopeText}, "
            + $"kizuna={kizunaFilter.Text}, source={context.Source}, diagnostics={context.Diagnostics}, "
            + $"candidates={result.CandidateCount}, eligible={result.UsableCount}, "
            + $"existingInvited={result.ExistingControlledCount}, invited={result.InvitedCount}, "
            + $"skipped={result.SkippedCount}");
        return result;
    }

    private static InvitationContext ReadInvitationContext(
        DataRepository? repository,
        RareGuestInvitationScope scope)
    {
        if (!RuntimeSceneReadinessCapture.CanReadDaySceneRuntime())
        {
            return InvitationContext.Failed(
                Fail("日间场景运行时尚未稳定。请等待场景加载完成后再试。"));
        }

        var dataBaseCharacterType = RuntimeReflectionUtility.FindType(DataBaseCharacterTypeName);
        var dataBaseDayType = RuntimeReflectionUtility.FindType(DataBaseDayTypeName);
        var albumType = RuntimeReflectionUtility.FindType(RunTimeAlbumTypeName);
        var statusTrackerType = RuntimeReflectionUtility.FindType(StatusTrackerTypeName);
        var npcType = RuntimeReflectionUtility.FindType(NpcTypeName);
        var specialGuestRunTimeDataType = RuntimeReflectionUtility.FindType(SpecialGuestRunTimeDataTypeName);
        if (dataBaseCharacterType == null
            || dataBaseDayType == null
            || albumType == null
            || statusTrackerType == null
            || npcType == null
            || specialGuestRunTimeDataType == null)
        {
            return InvitationContext.Failed(
                Fail("游戏原生羁绊邀请系统尚未初始化。请在读取存档后的日间场景再试。"));
        }

        var currentMap = ReadCurrentDaySceneMapInfo();
        if (string.IsNullOrWhiteSpace(currentMap.Label))
        {
            return InvitationContext.Failed(
                Fail("当前日间地图尚未就绪。请等待场景加载完成后再试。"));
        }

        var statusTracker = ReadExactSingletonInstance(
            statusTrackerType,
            SingletonTypeDefinitionName);
        if (statusTracker == null)
        {
            return InvitationContext.Failed(
                Fail("未读取到游戏邀请状态。请在读取存档后的日间场景再试。"));
        }

        if (repository == null)
        {
            return InvitationContext.Failed(
                Fail("运行时稀客目录尚未初始化。请等待存档数据读取完成后再试。"));
        }

        var mappedGuestSnapshot = new RuntimeMappedGuestCatalog(repository).Snapshot();
        if (!mappedGuestSnapshot.IsComplete)
        {
            return InvitationContext.Failed(
                Fail($"运行时稀客身份目录不可用：{mappedGuestSnapshot.Status}"));
        }

        var allNpcs = ReadRequiredStaticDictionaryProperty(
            dataBaseDayType,
            "allNPCs",
            typeof(string),
            valueType => valueType == npcType,
            npcType.FullName ?? NpcTypeName);
        Type? runtimeDaySceneType = null;
        Type? runTimePlayerDataType = null;
        object? trackedNpcs = null;
        if (scope == RareGuestInvitationScope.CurrentScene)
        {
            runtimeDaySceneType = RuntimeReflectionUtility.FindType(RuntimeDaySceneTypeName);
            runTimePlayerDataType = RuntimeReflectionUtility.FindType(RunTimePlayerDataTypeName);
            var trackedNpcType = RuntimeReflectionUtility.FindType(TrackedNpcTypeName);
            if (runtimeDaySceneType == null
                || runTimePlayerDataType == null
                || trackedNpcType == null)
            {
                return InvitationContext.Failed(
                    Fail("当前日间场景的稀客状态尚未初始化。请等待场景加载完成后再试。"));
            }

            trackedNpcs = ReadRequiredStaticDictionaryProperty(
                runtimeDaySceneType,
                "trackedNPCs",
                typeof(string),
                valueType => IsClosedIl2CppDictionary(valueType, typeof(string), trackedNpcType),
                $"Dictionary<String,{trackedNpcType.FullName ?? TrackedNpcTypeName}>");
        }

        var recordedSpecialNpcs = ReadRequiredStaticDictionaryProperty(
            albumType,
            "RecordedSpecialNPCs",
            typeof(int),
            valueType => valueType == specialGuestRunTimeDataType,
            specialGuestRunTimeDataType.FullName ?? SpecialGuestRunTimeDataTypeName);
        var baseGuestsById = ReadBaseSpecialGuests(dataBaseCharacterType);
        var candidates = ReadInviteCandidates(
            mappedGuestSnapshot,
            baseGuestsById,
            runtimeDaySceneType,
            runTimePlayerDataType,
            allNpcs,
            trackedNpcs,
            recordedSpecialNpcs,
            currentMap,
            scope,
            out var diagnostics);
        var source = scope == RareGuestInvitationScope.AllScenes
            ? "all-day-npcs-keyed"
            : "current-day-scene-keyed";
        if (candidates.Count == 0)
        {
            var failed = Fail(scope == RareGuestInvitationScope.AllScenes
                ? "未读取到可邀请稀客候选。请确认已读取存档并处于日间场景。"
                : "当前日间场景没有稀客邀请候选。");
            failed.RuntimeAvailable = true;
            failed.Scope = ScopeToText(scope);
            failed.Source = source;
            failed.Diagnostics = diagnostics;
            failed.CurrentMapLabel = currentMap.Label;
            failed.CurrentMapName = currentMap.Name;
            return InvitationContext.Failed(failed);
        }

        return new InvitationContext
        {
            Ok = true,
            StatusTracker = statusTracker,
            Candidates = candidates,
            Source = source,
            Diagnostics = diagnostics,
            Scope = scope,
            CurrentMap = currentMap,
        };
    }

    private static RareGuestInvitationResult CreateBaseResult(InvitationContext context)
    {
        return new RareGuestInvitationResult
        {
            RuntimeAvailable = true,
            Source = context.Source,
            Diagnostics = context.Diagnostics,
            Scope = context.ScopeText,
            CurrentMapLabel = context.CurrentMap.Label,
            CurrentMapName = context.CurrentMap.Name,
            ExistingSlotCount = ReadRequiredCollectionCount(
                ReadRequiredMember(context.StatusTracker!, "InvitedGuests"),
                "StatusTracker.InvitedGuests"),
        };
    }

    private static void ProcessCandidate(
        RareGuestInvitationResult result,
        object statusTracker,
        RuntimeRareGuestInvitationCandidate candidate,
        bool writeInvitation,
        KizunaLevelFilter? kizunaFilter = null,
        RareGuestInvitationWriteExpectation writeExpectation = default)
    {
        if (kizunaFilter is { IsEmpty: false }
            && (!candidate.KizunaStateKnown || !kizunaFilter.Matches(candidate.KizunaLevel)))
        {
            return;
        }

        result.CandidateCount++;
        if (kizunaFilter != null) result.KizunaLevelFilter = kizunaFilter.Text;

        if (candidate.AvailabilityKnown && !candidate.RuntimeAvailable)
        {
            AddUnavailableCandidate(
                result,
                candidate,
                "unavailable",
                string.IsNullOrWhiteSpace(candidate.AvailabilityReason)
                    ? "当前时间或日间状态不可见"
                    : candidate.AvailabilityReason,
                candidate.KizunaLevel);
            return;
        }

        if (!candidate.KizunaStateKnown)
        {
            AddUnavailableCandidate(
                result,
                candidate,
                "kizuna-uninitialized",
                "游戏尚未初始化该稀客的羁绊记录",
                -1);
            return;
        }

        if (HasNpcInvited(statusTracker, candidate.CanonicalGuestId))
        {
            result.ExistingControlledCount++;
            var existingInvitedEntry = BuildEntry(
                candidate,
                "invited",
                false,
                "今晚已邀请",
                candidate.KizunaLevel);
            result.Candidates.Add(existingInvitedEntry);
            result.ExistingInvited.Add(existingInvitedEntry);
            result.Skipped.Add(existingInvitedEntry);
            return;
        }

        if (candidate.KizunaLevel < 2)
        {
            AddUnavailableCandidate(
                result,
                candidate,
                "low-kizuna",
                $"羁绊等级不足 {candidate.KizunaLevel}",
                candidate.KizunaLevel);
            return;
        }

        if (!HasInviteDialog(candidate.Guest, candidate.KizunaLevel, succeed: true))
        {
            AddUnavailableCandidate(
                result,
                candidate,
                "missing-dialog",
                $"当前羁绊等级无成功邀请对话 {candidate.KizunaLevel}",
                candidate.KizunaLevel);
            return;
        }

        result.UsableCount++;
        var availableEntry = BuildEntry(
            candidate,
            "available",
            true,
            $"可邀请（羁绊 {candidate.KizunaLevel}）",
            candidate.KizunaLevel);
        if (!writeInvitation)
        {
            result.Candidates.Add(availableEntry);
            result.Available.Add(availableEntry);
            return;
        }

        if (!TryValidateWriteExpectation(writeExpectation, out var waitReason))
        {
            throw new InvalidOperationException(waitReason);
        }

        RecordInvitedGuest(statusTracker, candidate.CanonicalGuestId);
        if (!HasNpcInvited(statusTracker, candidate.CanonicalGuestId))
        {
            AddUnavailableCandidate(
                result,
                candidate,
                "failed",
                "原生记录邀请失败",
                candidate.KizunaLevel);
            return;
        }

        var invitedEntry = BuildEntry(
            candidate,
            "invited",
            false,
            $"已按原生羁绊邀请条件加入今晚名单（羁绊 {candidate.KizunaLevel}）",
            candidate.KizunaLevel);
        result.Candidates.Add(invitedEntry);
        result.Invited.Add(invitedEntry);
    }

    private static IReadOnlyList<RuntimeRareGuestInvitationCandidate> ReadInviteCandidates(
        RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot,
        IReadOnlyDictionary<int, object> baseGuestsById,
        Type? runtimeDaySceneType,
        Type? runTimePlayerDataType,
        object allNpcs,
        object? trackedNpcs,
        object recordedSpecialNpcs,
        DaySceneMapInfo currentMap,
        RareGuestInvitationScope scope,
        out string diagnostics)
    {
        object? currentTrackedMap = null;
        var currentTrackedMapFound = false;
        var remainActions = 0;
        if (scope == RareGuestInvitationScope.CurrentScene)
        {
            if (runtimeDaySceneType == null || runTimePlayerDataType == null || trackedNpcs == null)
            {
                throw new InvalidOperationException(
                    "Current-scene invitation candidates require initialized day-scene runtime types.");
            }

            currentTrackedMapFound = TryGetRequiredDictionaryValue(
                trackedNpcs,
                currentMap.Label,
                "RunTimeDayScene.trackedNPCs",
                out currentTrackedMap);
            if (currentTrackedMapFound && currentTrackedMap == null)
            {
                throw new InvalidOperationException(
                    $"RunTimeDayScene.trackedNPCs['{currentMap.Label}'] returned null.");
            }

            remainActions = ReadRequiredStaticIntProperty(runtimeDaySceneType, "RemainActions");
        }

        var candidates = new List<RuntimeRareGuestInvitationCandidate>();
        var dayNpcCount = 0;
        var missingDayNpcCount = 0;
        var noDayDestinationCount = 0;
        var trackedCount = 0;
        var missingTrackedCount = 0;
        var kizunaCount = 0;
        var missingKizunaCount = 0;
        var availabilityErrorCount = 0;
        var availabilityErrorSamples = new List<string>();

        foreach (var identity in mappedGuestSnapshot.Entries
                     .OrderBy(entry => entry.RuntimeId ?? int.MaxValue)
                     .ThenBy(entry => entry.RuntimeStringId, StringComparer.Ordinal))
        {
            if (identity.RuntimeId is not { } runtimeId
                || runtimeId < 0
                || identity.SourceGuestId is not { } sourceGuestId
                || sourceGuestId < 0
                || string.IsNullOrWhiteSpace(identity.RuntimeStringId)
                || string.IsNullOrWhiteSpace(identity.SourceStringId))
            {
                throw new InvalidOperationException("Runtime mapped guest snapshot contains an incomplete identity.");
            }

            if (!TryGetRequiredDictionaryValue(
                    allNpcs,
                    identity.RuntimeStringId,
                    "DataBaseDay.allNPCs",
                    out var npc))
            {
                missingDayNpcCount++;
                continue;
            }

            if (npc == null)
            {
                throw new InvalidOperationException(
                    $"DataBaseDay.allNPCs['{identity.RuntimeStringId}'] returned null.");
            }

            dayNpcCount++;
            var npcKey = ReadRequiredString(npc, "key");
            if (!string.Equals(npcKey, identity.RuntimeStringId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"DataBaseDay.allNPCs key '{identity.RuntimeStringId}' resolved NPC '{npcKey}'.");
            }

            var npcIdentity = ReadRequiredMember(npc, "identity");
            var characterId = ReadRequiredNonNegativeInt(npcIdentity, "characterId");
            var invitationIdentity = RuntimeRareGuestInvitationIdentity.Resolve(
                runtimeId,
                sourceGuestId,
                characterId,
                identity.RuntimeStringId);

            if (!baseGuestsById.TryGetValue(invitationIdentity.CanonicalGuestId, out var guest))
            {
                throw new InvalidOperationException(
                    $"Mapped identity '{identity.RuntimeStringId}' has missing base guest "
                    + $"{invitationIdentity.CanonicalGuestId}.");
            }

            var baseStringId = ReadRequiredString(guest, "stringId");
            if (!string.Equals(baseStringId, identity.SourceStringId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Mapped identity '{identity.RuntimeStringId}' does not match base guest {sourceGuestId}.");
            }

            if (scope == RareGuestInvitationScope.AllScenes
                && !HasNpcDayDestination(
                    npc,
                    identity.RuntimeStringId,
                    invitationIdentity.RuntimeId,
                    invitationIdentity.CanonicalGuestId))
            {
                noDayDestinationCount++;
                continue;
            }

            object? trackedNpc = null;
            var trackedFound = currentTrackedMapFound
                && TryGetRequiredDictionaryValue(
                    currentTrackedMap!,
                    identity.RuntimeStringId,
                    $"RunTimeDayScene.trackedNPCs['{currentMap.Label}']",
                    out trackedNpc);
            if (trackedFound)
            {
                if (trackedNpc == null)
                {
                    throw new InvalidOperationException(
                        $"RunTimeDayScene.trackedNPCs['{currentMap.Label}']"
                        + $"['{identity.RuntimeStringId}'] returned null.");
                }

                trackedCount++;
            }
            else if (scope == RareGuestInvitationScope.CurrentScene)
            {
                missingTrackedCount++;
            }

            var isCurrentScene = trackedFound;
            if (scope == RareGuestInvitationScope.CurrentScene && !trackedFound) continue;

            var runtimeAvailable = true;
            var availabilityKnown = false;
            var availabilityReason = "";
            if (scope == RareGuestInvitationScope.CurrentScene)
            {
                availabilityKnown = true;
                try
                {
                    runtimeAvailable = ReadTrackedNpcAvailability(
                        trackedNpc!,
                        npc,
                        runTimePlayerDataType!,
                        remainActions);
                }
                catch (Exception ex)
                {
                    runtimeAvailable = false;
                    availabilityReason = "当前日间可见状态读取失败";
                    availabilityErrorCount++;
                    if (availabilityErrorSamples.Count < 4)
                    {
                        availabilityErrorSamples.Add(
                            $"{identity.RuntimeStringId}:{ex.GetType().Name}:{ex.Message}");
                    }
                }
            }

            var kizunaStateKnown = TryGetRequiredDictionaryValue(
                recordedSpecialNpcs,
                invitationIdentity.CanonicalGuestId,
                "RunTimeAlbum.RecordedSpecialNPCs",
                out var kizunaData);
            var kizunaLevel = -1;
            if (kizunaStateKnown)
            {
                if (kizunaData == null)
                {
                    throw new InvalidOperationException(
                        $"RunTimeAlbum.RecordedSpecialNPCs[{characterId}] returned null.");
                }

                kizunaLevel = ReadRequiredNonNegativeInt(kizunaData, "CurrentBondLevel");
                kizunaCount++;
            }
            else
            {
                missingKizunaCount++;
            }

            var normalizedSceneLabels = trackedFound
                ? new List<string> { currentMap.Label }
                : new List<string>();
            candidates.Add(new RuntimeRareGuestInvitationCandidate(
                Guest: guest,
                CanonicalGuestId: invitationIdentity.CanonicalGuestId,
                RuntimeId: invitationIdentity.RuntimeId,
                RuntimeName: identity.RuntimeStringId,
                DisplayName: ResolveGuestName(identity, guest),
                SceneLabels: normalizedSceneLabels,
                SceneNames: normalizedSceneLabels
                    .Select(NormalizePlaceName)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                IsCurrentScene: isCurrentScene,
                AvailabilityKnown: availabilityKnown,
                RuntimeAvailable: runtimeAvailable,
                AvailabilityReason: availabilityReason,
                KizunaStateKnown: kizunaStateKnown,
                KizunaLevel: kizunaLevel));
        }

        if (scope == RareGuestInvitationScope.CurrentScene
            && candidates.Count > 0
            && availabilityErrorCount == candidates.Count)
        {
            throw new InvalidOperationException(
                $"All {candidates.Count} current-scene candidate visibility reads failed. "
                + $"Samples: {string.Join(" | ", availabilityErrorSamples)}");
        }

        var canonicalCandidates = RuntimeRareGuestInvitationCandidates.Deduplicate(candidates);
        var mappedIdentitySamples = candidates
            .Where(candidate => candidate.RuntimeId != candidate.CanonicalGuestId)
            .OrderBy(candidate => candidate.CanonicalGuestId)
            .ThenBy(candidate => candidate.RuntimeId)
            .Take(4)
            .Select(candidate =>
                $"{candidate.RuntimeName}:{candidate.RuntimeId}->{candidate.CanonicalGuestId}")
            .ToList();
        var trackedMapStatus = scope == RareGuestInvitationScope.AllScenes
            ? "not-read"
            : currentTrackedMapFound ? "ready" : "missing";
        diagnostics =
            $"identities={mappedGuestSnapshot.Entries.Count}; dayNpcs={dayNpcCount}; "
            + $"missingDayNpcs={missingDayNpcCount}; "
            + $"runtimeNoDayDestinations={noDayDestinationCount}; "
            + $"trackedMap={trackedMapStatus}; "
            + $"tracked={trackedCount}; missingTracked={missingTrackedCount}; "
            + $"availabilityErrors={availabilityErrorCount}; "
            + $"runtimeKizuna={kizunaCount}; runtimeMissingKizuna={missingKizunaCount}; "
            + $"runtimeCandidates={candidates.Count}; candidates={canonicalCandidates.Count}; "
            + $"mergedAliases={candidates.Count - canonicalCandidates.Count}"
            + (mappedIdentitySamples.Count > 0
                ? $"; mappedIdentitySamples={string.Join(" | ", mappedIdentitySamples)}"
                : "")
            + (availabilityErrorSamples.Count > 0
                ? $"; availabilityErrorSamples={string.Join(" | ", availabilityErrorSamples)}"
                : "");
        return canonicalCandidates;
    }

    private static bool HasNpcDayDestination(
        object npc,
        string runtimeStringId,
        int runtimeId,
        int canonicalGuestId)
    {
        var context =
            $"NPC '{runtimeStringId}' (runtime ID {runtimeId}, canonical guest {canonicalGuestId})";
        var possibleDestinations = RuntimeReflectionUtility.GetMemberValue(npc, "possibleDestinations");
        if (possibleDestinations == null) return false;
        if (!RuntimeConcreteCollectionReader.TryReadReferenceArray(
                possibleDestinations,
                out var destinations,
                out var failure))
        {
            throw new InvalidOperationException(
                $"{context} possibleDestinations returned an unreadable array: {failure}.");
        }

        if (destinations.Count > MaxInvitationGuestCount)
        {
            throw new InvalidOperationException(
                $"{context} possibleDestinations exceeded the "
                + $"{MaxInvitationGuestCount}-item limit.");
        }

        if (destinations.Any(destination => destination == null))
        {
            throw new InvalidOperationException(
                $"{context} possibleDestinations contains a null entry.");
        }

        return destinations.Count > 0;
    }

    private static bool ReadTrackedNpcAvailability(
        object trackedNpc,
        object npc,
        Type runTimePlayerDataType,
        int remainActions)
    {
        var overridePosition = ReadRequiredExactInstancePropertyValue(
            trackedNpc,
            "overridePosition",
            allowNull: true);
        if (overridePosition != null) return true;

        var identity = ReadRequiredExactInstancePropertyValue(npc, "identity");
        var hasNormalIdentity = RuntimeSchedulerCharacterIdentity.IsNormal(identity!);

        var currentDestination = ReadRequiredExactInstancePropertyValue(trackedNpc, "currentDestination");
        var defaultDestination = ReadRequiredExactStaticPropertyValue(
            npc.GetType(),
            "defaultDestination",
            currentDestination!.GetType());
        var showTime = ReadRequiredExactInstancePropertyValue(npc, "showTime");

        return RuntimeTrackedNpcAvailability.Evaluate(new RuntimeTrackedNpcAvailabilityInput(
            HasOverridePosition: false,
            HasNormalIdentity: hasNormalIdentity,
            ShouldShowSpecialGuestsInDay: ReadRequiredExactStaticBooleanProperty(
                runTimePlayerDataType,
                "ShouldShowSpecialGuestsInDay"),
            CurrentSpawnMarker: ReadRequiredExactStringProperty(currentDestination, "spawnMarker"),
            HiddenSpawnMarker: ReadRequiredExactStringProperty(defaultDestination, "spawnMarker"),
            OpenStatus: ReadRequiredExactBooleanProperty(trackedNpc, "openStatus"),
            RestDays: ReadRequiredExactInt32Property(trackedNpc, "restDays"),
            ShowTimeStart: ReadRequiredExactInt32Property(showTime!, "x"),
            ShowTimeEnd: ReadRequiredExactInt32Property(showTime!, "y"),
            RemainActions: remainActions));
    }

    private static IReadOnlyDictionary<int, object> ReadBaseSpecialGuests(Type dataBaseCharacterType)
    {
        var method = RequireExactStaticMethod(
            dataBaseCharacterType,
            "GetAllSpecialGuests",
            returnType: null);
        var source = InvokeRequired(method, null)
            ?? throw new InvalidOperationException("GetAllSpecialGuests returned null.");
        if (!RuntimeConcreteCollectionReader.TryReadReferenceArray(source, out var guests, out var failure))
        {
            throw new InvalidOperationException(
                $"GetAllSpecialGuests returned an unreadable array: {failure}.");
        }

        if (guests.Count == 0 || guests.Count > MaxInvitationGuestCount)
        {
            throw new InvalidOperationException(
                $"GetAllSpecialGuests returned invalid count {guests.Count}.");
        }

        var result = new Dictionary<int, object>();
        var stringIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guest in guests)
        {
            if (guest == null) throw new InvalidOperationException("GetAllSpecialGuests contains a null entry.");
            var id = ReadRequiredNonNegativeInt(guest, "id");
            var stringId = ReadRequiredString(guest, "stringId");
            if (!result.TryAdd(id, guest) || !stringIds.Add(stringId))
            {
                throw new InvalidOperationException(
                    $"GetAllSpecialGuests contains duplicate ID or StringId at {id}/{stringId}.");
            }
        }

        return result;
    }

    private static bool TryGetRequiredDictionaryValue(
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
                $"{source} does not support an exact keyed read for '{key}': {failure}.");
        }

        return found;
    }

    private static object ReadRequiredStaticDictionaryProperty(
        Type type,
        string propertyName,
        Type expectedKeyType,
        Func<Type, bool> isExpectedValueType,
        string expectedValueType)
    {
        var property = RequireExactProperty(
            type,
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (!TryGetClosedIl2CppDictionaryArguments(
                property.PropertyType,
                out var keyType,
                out var valueType)
            || keyType != expectedKeyType
            || !isExpectedValueType(valueType))
        {
            throw new InvalidOperationException(
                $"{type.FullName}.{propertyName} must be a closed "
                + $"Dictionary<{expectedKeyType.FullName},{expectedValueType}> property.");
        }

        var value = ReadRequiredPropertyValue(property, null, allowNull: false)!;
        if (value.GetType() != property.PropertyType)
        {
            throw new InvalidOperationException(
                $"{type.FullName}.{propertyName} returned runtime type "
                + $"{value.GetType().FullName}, expected {property.PropertyType.FullName}.");
        }

        return value;
    }

    private static bool IsClosedIl2CppDictionary(
        Type type,
        Type expectedKeyType,
        Type expectedValueType)
    {
        return TryGetClosedIl2CppDictionaryArguments(type, out var keyType, out var valueType)
            && keyType == expectedKeyType
            && valueType == expectedValueType;
    }

    private static bool TryGetClosedIl2CppDictionaryArguments(
        Type type,
        out Type keyType,
        out Type valueType)
    {
        keyType = null!;
        valueType = null!;
        if (!type.IsGenericType || type.ContainsGenericParameters) return false;
        if (!string.Equals(
                type.GetGenericTypeDefinition().FullName,
                Il2CppDictionaryTypeName,
                StringComparison.Ordinal))
        {
            return false;
        }

        var arguments = type.GetGenericArguments();
        if (arguments.Length != 2) return false;
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static int ReadRequiredStaticIntProperty(Type type, string propertyName)
    {
        var property = RequireExactProperty(
            type,
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property.PropertyType != typeof(int))
        {
            throw new MissingMemberException(type.FullName, propertyName);
        }

        var value = ReadRequiredPropertyValue(property, null, allowNull: false);
        return value is int result
            ? result
            : throw new InvalidOperationException(
                $"{type.FullName}.{propertyName} returned a non-Int32 value.");
    }

    private static bool ReadRequiredExactStaticBooleanProperty(Type type, string propertyName)
    {
        var property = RequireExactProperty(
            type,
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property.PropertyType != typeof(bool))
        {
            throw new MissingMemberException(type.FullName, propertyName);
        }

        var value = ReadRequiredPropertyValue(property, null, allowNull: false);
        return value is bool result
            ? result
            : throw new InvalidOperationException(
                $"{type.FullName}.{propertyName} returned a non-Boolean value.");
    }

    private static object? ReadRequiredExactStaticPropertyValue(
        Type type,
        string propertyName,
        Type expectedType)
    {
        var property = RequireExactProperty(
            type,
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (property.PropertyType != expectedType)
        {
            throw new MissingMemberException(type.FullName, propertyName);
        }

        return ReadRequiredPropertyValue(property, null, allowNull: false);
    }

    private static object? ReadRequiredExactInstancePropertyValue(
        object instance,
        string propertyName,
        bool allowNull = false)
    {
        var property = RequireExactProperty(
            instance.GetType(),
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return ReadRequiredPropertyValue(property, instance, allowNull);
    }

    private static bool ReadRequiredExactBooleanProperty(object instance, string propertyName)
    {
        var value = ReadRequiredExactInstancePropertyValue(instance, propertyName);
        return value is bool result
            ? result
            : throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{propertyName} returned a non-Boolean value.");
    }

    private static int ReadRequiredExactInt32Property(object instance, string propertyName)
    {
        var value = ReadRequiredExactInstancePropertyValue(instance, propertyName);
        return value is int result
            ? result
            : throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{propertyName} returned a non-Int32 value.");
    }

    private static string ReadRequiredExactStringProperty(object? instance, string propertyName)
    {
        if (instance == null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        var value = ReadRequiredExactInstancePropertyValue(instance, propertyName);
        return value is string result
            ? result
            : throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{propertyName} returned a non-String value.");
    }

    private static PropertyInfo RequireExactProperty(
        Type type,
        string propertyName,
        BindingFlags flags)
    {
        var matches = type
            .GetProperties(flags | BindingFlags.DeclaredOnly)
            .Where(property =>
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal)
                    || property.GetIndexParameters().Length != 0)
                {
                    return false;
                }

                var getter = property.GetGetMethod(nonPublic: true);
                return getter != null
                    && getter.IsStatic == flags.HasFlag(BindingFlags.Static);
            })
            .Take(2)
            .ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new MissingMemberException(type.FullName, propertyName);
    }

    private static object? ReadRequiredPropertyValue(
        PropertyInfo property,
        object? instance,
        bool allowNull)
    {
        try
        {
            var value = property.GetValue(instance);
            if (value == null && !allowNull)
            {
                throw new InvalidOperationException(
                    $"{property.DeclaringType?.FullName}.{property.Name} is null.");
            }

            return value;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static MethodInfo RequireExactStaticMethod(
        Type type,
        string methodName,
        Type? returnType,
        params Type[] parameterTypes)
    {
        return RequireExactMethod(
            type,
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            returnType,
            parameterTypes);
    }

    private static MethodInfo RequireExactInstanceMethod(
        Type type,
        string methodName,
        Type? returnType,
        params Type[] parameterTypes)
    {
        return RequireExactMethod(
            type,
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            returnType,
            parameterTypes);
    }

    private static MethodInfo RequireExactMethod(
        Type type,
        string methodName,
        BindingFlags flags,
        Type? returnType,
        IReadOnlyList<Type> parameterTypes)
    {
        var matches = type
            .GetMethods(flags)
            .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal)
                && !method.IsGenericMethod
                && (returnType == null || method.ReturnType == returnType))
            .Where(method =>
            {
                var parameters = method.GetParameters();
                if (parameters.Length != parameterTypes.Count) return false;
                for (var index = 0; index < parameters.Length; index++)
                {
                    if (parameters[index].ParameterType != parameterTypes[index]) return false;
                }

                return true;
            })
            .Take(2)
            .ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new MissingMethodException(type.FullName, methodName);
    }

    private static object? InvokeRequired(MethodInfo method, object? instance, params object?[] arguments)
    {
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static string ReadCurrentMapLabel(Type? sceneManagerType)
    {
        var sceneManager = ResolveSceneManager(sceneManagerType);
        var current = RuntimeReflectionUtility.GetMemberValue(sceneManager, "CurrentActiveMapLabel");
        return current is string text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : "";
    }

    private static object? ResolveSceneManager(Type? sceneManagerType)
    {
        if (sceneManagerType == null) return null;
        return ReadExactSingletonInstance(
            sceneManagerType,
            MonoSingletonTypeDefinitionName);
    }

    private static string NormalizePlaceName(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var languageType = RuntimeReflectionUtility.FindType(DaySceneLanguageTypeName);
        if (languageType == null) return label.Trim();

        try
        {
            var method = RequireExactStaticMethod(
                languageType,
                "GetMapLanguageData",
                returnType: null,
                typeof(string));
            var language = InvokeRequired(method, null, label);
            foreach (var member in new[] { "Text", "Name", "Value", "content", "text", "name", "value" })
            {
                var value = RuntimeReflectionUtility.GetMemberValue(language, member);
                if (value is string text && !string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
        }
        catch
        {
            // The stable map label remains a valid display fallback.
        }

        return label.Trim();
    }

    private static bool HasInviteDialog(object guest, int level, bool succeed)
    {
        var method = RequireExactInstanceMethod(
            guest.GetType(),
            "GetInviteDialogPackageAtKizunaLevel",
            returnType: null,
            typeof(int),
            typeof(bool));
        var dialogs = InvokeRequired(method, guest, level, succeed);
        if (dialogs == null) return false;
        if (!RuntimeConcreteCollectionReader.TryReadReferenceArray(dialogs, out var values, out var failure))
        {
            throw new InvalidOperationException(
                $"{guest.GetType().FullName}.GetInviteDialogPackageAtKizunaLevel "
                + $"returned an unreadable array: {failure}.");
        }

        return values.Count > 0;
    }

    private static bool HasNpcInvited(object statusTracker, int id)
    {
        var method = RequireExactInstanceMethod(
            statusTracker.GetType(),
            "HasNPCInvited",
            typeof(bool),
            typeof(int));
        var value = InvokeRequired(method, statusTracker, id);
        return value is bool result
            ? result
            : throw new InvalidOperationException(
                $"{statusTracker.GetType().FullName}.HasNPCInvited returned a non-Boolean value.");
    }

    private static void RecordInvitedGuest(object statusTracker, int id)
    {
        var method = RequireExactInstanceMethod(
            statusTracker.GetType(),
            "RecordInvitedGuest",
            typeof(void),
            typeof(int));
        InvokeRequired(method, statusTracker, id);
    }

    private static object? ReadExactSingletonInstance(
        Type concreteType,
        string expectedGenericTypeDefinitionName)
    {
        try
        {
            var singletonType = concreteType.BaseType;
            if (singletonType == null
                || !singletonType.IsGenericType
                || !string.Equals(
                    singletonType.GetGenericTypeDefinition().FullName,
                    expectedGenericTypeDefinitionName,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var genericArguments = singletonType.GetGenericArguments();
            if (genericArguments.Length != 1 || genericArguments[0] != concreteType)
            {
                return null;
            }

            var property = RequireExactProperty(
                singletonType,
                "Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (property.PropertyType != concreteType)
            {
                return null;
            }

            var instance = ReadRequiredPropertyValue(property, null, allowNull: true);
            return instance != null && concreteType.IsInstanceOfType(instance)
                ? instance
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool TryValidateWriteExpectation(
        RareGuestInvitationWriteExpectation expectation,
        out string reason)
    {
        if (!RuntimeSceneReadinessCapture.CanReadDaySceneRuntime())
        {
            reason = "日间场景运行时已离开就绪状态，本次邀请未执行。";
            return false;
        }

        var currentMap = ReadCurrentDaySceneMapInfo();
        return RuntimeRareGuestInvitationWriteGuard.Matches(
            expectation,
            RuntimeSceneReadinessCapture.DaySceneGeneration,
            currentMap.Label,
            out reason);
    }

    private static int ReadRequiredCollectionCount(object collection, string source)
    {
        var value = RuntimeReflectionUtility.GetMemberValue(collection, "Count");
        if (value is not int count || count < 0 || count > MaxInvitationGuestCount)
        {
            throw new InvalidOperationException($"{source} returned an invalid Count.");
        }

        return count;
    }

    private static object ReadRequiredMember(object instance, string memberName)
    {
        return RuntimeReflectionUtility.GetMemberValue(instance, memberName)
            ?? throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} is unavailable.");
    }

    private static int ReadRequiredNonNegativeInt(object instance, string memberName)
    {
        var value = ReadRequiredMember(instance, memberName);
        if (value is not int result || result < 0)
        {
            throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} returned an invalid Int32 value.");
        }

        return result;
    }

    private static string ReadRequiredString(object instance, string memberName)
    {
        var value = ReadRequiredMember(instance, memberName);
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"{instance.GetType().FullName}.{memberName} returned an invalid String value.");
        }

        return text.Trim();
    }

    private static void AddUnavailableCandidate(
        RareGuestInvitationResult result,
        RuntimeRareGuestInvitationCandidate candidate,
        string status,
        string reason,
        int kizunaLevel)
    {
        var entry = BuildEntry(candidate, status, false, reason, kizunaLevel);
        result.Candidates.Add(entry);
        result.Skipped.Add(entry);
    }

    private static RareGuestInvitationEntry BuildEntry(
        RuntimeRareGuestInvitationCandidate candidate,
        string status,
        bool canInvite,
        string reason,
        int kizunaLevel)
    {
        return new RareGuestInvitationEntry
        {
            Id = candidate.CanonicalGuestId,
            Name = candidate.DisplayName,
            RuntimeName = candidate.RuntimeName,
            Reason = reason,
            Status = status,
            CanInvite = canInvite,
            IsCurrentScene = candidate.IsCurrentScene,
            KizunaLevel = kizunaLevel,
            SceneLabels = candidate.SceneLabels.ToList(),
            SceneNames = candidate.SceneNames.ToList(),
        };
    }

    private static string BuildStatus(RareGuestInvitationResult result, string source)
    {
        var sourceLabel = source == "current-day-scene-keyed" ? "当前日间场景" : "全部日间场景";
        if (result.Invited.Count > 0) return $"{sourceLabel}已邀请 {result.Invited.Count} 位稀客。";
        if (result.UsableCount > 0) return $"{sourceLabel}没有新的可写入邀请，原生记录可能失败。";
        if (result.ExistingControlledCount > 0) return $"{sourceLabel}中的可邀请稀客今晚均已邀请。";
        return $"{sourceLabel}没有新的可邀请稀客。";
    }

    private static string BuildListStatus(RareGuestInvitationResult result, string source)
    {
        var sourceLabel = source == "current-day-scene-keyed" ? "当前日间场景" : "全部日间场景";
        if (result.Available.Count > 0) return $"{sourceLabel}有 {result.Available.Count} 位可邀请稀客。";
        if (result.ExistingControlledCount > 0) return $"{sourceLabel}中的可邀请稀客今晚均已邀请。";
        return $"{sourceLabel}没有新的可邀请稀客。";
    }

    private static string ResolveGuestName(RuntimeMappedGuestEntry identity, object guest)
    {
        if (!string.IsNullOrWhiteSpace(identity.LocalRareCustomerName))
        {
            return identity.LocalRareCustomerName;
        }

        var text = RuntimeReflectionUtility.GetMemberValue(guest, "Text")
            ?? RuntimeReflectionUtility.InvokeMethod(guest, "get_Text");
        var textName = RuntimeReflectionUtility.GetMemberValue(text, "Name");
        if (textName is string name && !string.IsNullOrWhiteSpace(name)) return name.Trim();
        if (!string.IsNullOrWhiteSpace(identity.SourceDisplayName)) return identity.SourceDisplayName;
        return identity.RuntimeStringId;
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

    private static KizunaLevelFilter ParseKizunaLevelFilter(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return KizunaLevelFilter.Empty;

        var levels = text
            .Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part.Trim(), out var value) ? value : -1)
            .Where(value => value >= 0)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        return levels.Length == 0
            ? KizunaLevelFilter.Empty
            : new KizunaLevelFilter(levels);
    }

    private static RareGuestInvitationScope ParseScope(string scopeText)
    {
        if (string.IsNullOrWhiteSpace(scopeText)
            || string.Equals(scopeText, "current", StringComparison.OrdinalIgnoreCase))
        {
            return RareGuestInvitationScope.CurrentScene;
        }

        if (string.Equals(scopeText, "all", StringComparison.OrdinalIgnoreCase))
        {
            return RareGuestInvitationScope.AllScenes;
        }

        throw new ArgumentException($"Unknown rare guest invitation scope '{scopeText}'.", nameof(scopeText));
    }

    private static string ScopeToText(RareGuestInvitationScope scope)
    {
        return scope == RareGuestInvitationScope.AllScenes ? "all" : "current";
    }

    private enum RareGuestInvitationScope
    {
        CurrentScene,
        AllScenes,
    }

    private sealed class KizunaLevelFilter
    {
        public static readonly KizunaLevelFilter Empty = new(Array.Empty<int>());
        private readonly HashSet<int> _levels;

        public KizunaLevelFilter(IReadOnlyCollection<int> levels)
        {
            _levels = levels.Count == 0
                ? new HashSet<int>()
                : new HashSet<int>(levels);
            Text = _levels.Count == 0
                ? ""
                : string.Join(",", _levels.OrderBy(level => level));
        }

        public bool IsEmpty => _levels.Count == 0;
        public string Text { get; }
        public bool Matches(int level) => IsEmpty || _levels.Contains(level);
    }

    internal sealed class DaySceneMapInfo
    {
        public string Label { get; init; } = "";
        public string Name { get; init; } = "";
    }

    private sealed class InvitationContext
    {
        public bool Ok { get; init; }
        public RareGuestInvitationResult Result { get; init; } = new();
        public object? StatusTracker { get; init; }
        public IReadOnlyList<RuntimeRareGuestInvitationCandidate> Candidates { get; init; } =
            Array.Empty<RuntimeRareGuestInvitationCandidate>();
        public string Source { get; init; } = "";
        public string Diagnostics { get; init; } = "";
        public RareGuestInvitationScope Scope { get; init; }
        public string ScopeText => ScopeToText(Scope);
        public DaySceneMapInfo CurrentMap { get; init; } = new();

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
