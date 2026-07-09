using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeSpecialBusinessContextService
{
    private const string NightSceneDirectorTypeName = "NightScene.NightSceneDirector";
    private const string IncomeControllerYuumaTypeName = "NightScene.UI.HUDUtility.IncomeControllerYuuma";
    private const string IncomeControllerKoishiTypeName = "NightScene.UI.HUDUtility.IncomeControllerKoishi";
    private const string IncomeControllerYuyukoTypeName = "NightScene.UI.HUDUtility.IncomeControllerYuyuko";
    private const string IncomeControllerChallengeTypeName = "NightScene.UI.HUDUtility.IncomeControllerChallenge";
    private const string IncomeControllerMausoleumTypeName = "NightScene.UI.HUDUtility.IncomeControllerMausoleumCuisineCompetition";
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string KoishiClueGenerationPatchKey = "GameData.Profile.DLC2_KoishiBossData.DisplayClass13_0.KoishiClueTagsGenerated";
    private const string KoishiClueGenerationRuntimeMethodName = "Method_Internal_Void_2";
    private const string NoDifficultyMode = "None";

    private static readonly object SyncRoot = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static DateTime _lastAttachAttemptUtc = DateTime.MinValue;
    private static string _status = "not attached";
    private static string _lastAction = "";
    private static string _targetKind = "";
    private static string[] _foodTargetTags = Array.Empty<string>();
    private static int? _targetFund;
    private static string _targetLabel = "";
    private static string _phase = "";
    private static int? _currentValue;
    private static int? _maxValue;
    private static int? _targetValue;
    private static double? _targetTimeProgress;
    private static double? _targetTagTimeProgress;
    private static bool? _koishiShieldBroken;
    private static string[] _koishiFoodPreferenceTags = Array.Empty<string>();
    private static string[] _koishiFoodHateTags = Array.Empty<string>();
    private static string[] _koishiBeveragePreferenceTags = Array.Empty<string>();
    private static int? _currentSpellCount;
    private static int? _targetSpellCount;
    private static DateTime? _lastTargetUpdatedUtc;
    private static string _yuyukoRetakeEvidenceSource = "";
    private static long _changeVersion;

    public static long ChangeVersion
    {
        get
        {
            lock (SyncRoot)
            {
                return _changeVersion;
            }
        }
    }

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                var target = _targetKind.Length == 0
                    ? "none"
                    : $"{_targetKind}; foodTags={string.Join(",", _foodTargetTags)}; targetFund={_targetFund?.ToString() ?? ""}; progress={_currentValue?.ToString() ?? ""}/{_maxValue?.ToString() ?? ""}; target={_targetValue?.ToString() ?? ""}; time={FormatProgress(_targetTimeProgress)}; tagTime={FormatProgress(_targetTagTimeProgress)}; koishiShield={_koishiShieldBroken?.ToString() ?? ""}; koishiFoodLikes={string.Join(",", _koishiFoodPreferenceTags)}; koishiFoodHates={string.Join(",", _koishiFoodHateTags)}; koishiBevLikes={string.Join(",", _koishiBeveragePreferenceTags)}; spells={_currentSpellCount?.ToString() ?? ""}/{_targetSpellCount?.ToString() ?? ""}; phase={_phase}";
                var yuyukoVariant = _yuyukoRetakeEvidenceSource.Length == 0
                    ? ""
                    : $"; yuyukoRetakeEvidence={_yuyukoRetakeEvidenceSource}";
                return $"{_status}; version={_changeVersion}; target={target}; last={_lastAction}{yuyukoVariant}";
            }
        }
    }

    public static string CurrentChallengeType => ReadChallengeTypeState(out _).EffectiveChallengeType;

    public static string CurrentRawChallengeType => ReadRawChallengeType(out _);

    public static bool IsRetakeYuyukoChallenge => string.Equals(CurrentChallengeType, SpecialBusinessChallengeTypes.RetakeYuyuko, StringComparison.Ordinal);

    public static void MarkYuyukoRetakeEvidence(string source)
    {
        var rawChallengeType = ReadRawChallengeType(out _);
        if (!IsYuyukoChallengeType(rawChallengeType)) return;

        var normalizedSource = CleanText(source);
        if (normalizedSource.Length == 0) normalizedSource = "runtime evidence";

        lock (SyncRoot)
        {
            SetYuyukoRetakeEvidenceLocked(normalizedSource);
        }
    }

    public static bool IsActiveWackyPhase(string phase)
    {
        if (!string.Equals(CurrentChallengeType, SpecialBusinessChallengeTypes.WackyCookingCompetition, StringComparison.Ordinal)) return false;

        lock (SyncRoot)
        {
            return string.Equals(_targetKind, "koishi", StringComparison.Ordinal)
                && string.Equals(_phase.Trim(), phase, StringComparison.Ordinal);
        }
    }

    public static bool IsWackyKoishiShieldBroken
    {
        get
        {
            if (!string.Equals(CurrentChallengeType, SpecialBusinessChallengeTypes.WackyCookingCompetition, StringComparison.Ordinal)) return false;

            lock (SyncRoot)
            {
                return string.Equals(_targetKind, "koishi", StringComparison.Ordinal)
                    && _koishiShieldBroken == true;
            }
        }
    }

    public static bool IsActiveYuyukoPhase(string phase)
    {
        var challengeType = CurrentChallengeType;
        if (!IsYuyukoChallengeType(challengeType))
        {
            return false;
        }

        lock (SyncRoot)
        {
            return string.Equals(_targetKind, "yuyuko", StringComparison.Ordinal)
                && string.Equals(_phase.Trim(), phase, StringComparison.Ordinal);
        }
    }

    public static string DescribeYuyukoProgressForDiagnostics()
    {
        lock (SyncRoot)
        {
            return $"kind={_targetKind}; label={_targetLabel}; phase={_phase}; current={_currentValue?.ToString() ?? ""}; max={_maxValue?.ToString() ?? ""}; target={_targetValue?.ToString() ?? ""}; time={FormatProgress(_targetTimeProgress)}; last={_lastAction}; version={_changeVersion}";
        }
    }

    public static bool TryGetActiveWackyFoodTargetTags(out IReadOnlyList<string> tags)
    {
        tags = Array.Empty<string>();
        if (!TryGetActiveWackyTargetSignature(out _, out var activeTags) || activeTags.Count == 0) return false;

        tags = activeTags;
        return true;
    }

    public static bool TryGetActiveWackyTargetSignature(out string signature, out IReadOnlyList<string> tags)
    {
        signature = "";
        tags = Array.Empty<string>();
        if (!string.Equals(CurrentChallengeType, SpecialBusinessChallengeTypes.WackyCookingCompetition, StringComparison.Ordinal)) return false;

        lock (SyncRoot)
        {
            if (!string.Equals(_targetKind, "koishi", StringComparison.Ordinal)) return false;

            var normalized = NormalizeFoodTargetTagsLocked();
            tags = normalized;
            signature = BuildWackyTargetSignatureLocked(normalized);
            return true;
        }
    }

    private static string[] NormalizeFoodTargetTagsLocked()
    {
        if (string.Equals(_targetKind, "koishi", StringComparison.Ordinal)
            && IsKoishiPhaseWithoutFoodTargetLocked(_phase))
        {
            return Array.Empty<string>();
        }

        return _foodTargetTags
            .Select(tag => FoodTags.NormalizeName(tag) ?? tag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildWackyTargetSignatureLocked(IReadOnlyList<string> normalizedFoodTags)
    {
        return $"{SpecialBusinessChallengeTypes.WackyCookingCompetition}|koishi|phase:{_phase.Trim()}|food:{string.Join(",", normalizedFoodTags)}";
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(log, force: true);
    }

    public static SpecialBusinessContext Snapshot()
    {
        TryAttach(_log, force: false);

        var challengeState = ReadChallengeTypeState(out var error);
        var challengeType = challengeState.EffectiveChallengeType;
        var active = IsActiveChallenge(challengeType);
        var rule = GetRule(challengeType, active);
        var target = ReadTargetForChallenge(challengeType);
        var source = BuildSource(challengeState, target);

        return new SpecialBusinessContext
        {
            Active = active,
            ChallengeType = challengeType,
            DisplayName = rule.DisplayName,
            Category = rule.Category,
            RuleSummary = rule.RuleSummary,
            FoodTargetTags = target.FoodTargetTags.ToList(),
            BeverageTargetTags = target.BeverageTargetTags.ToList(),
            TargetFund = target.TargetFund,
            TargetLabel = target.TargetLabel,
            Phase = target.Phase,
            CurrentValue = target.CurrentValue,
            MaxValue = target.MaxValue,
            TargetValue = target.TargetValue,
            TargetTimeProgress = target.TargetTimeProgress,
            TargetTagTimeProgress = target.TargetTagTimeProgress,
            WackyKoishiShieldBroken = target.WackyKoishiShieldBroken,
            WackyKoishiFoodPreferenceTags = target.WackyKoishiFoodPreferenceTags.ToList(),
            WackyKoishiFoodHateTags = target.WackyKoishiFoodHateTags.ToList(),
            WackyKoishiBeveragePreferenceTags = target.WackyKoishiBeveragePreferenceTags.ToList(),
            CurrentSpellCount = target.CurrentSpellCount,
            TargetSpellCount = target.TargetSpellCount,
            RecommendationPolicy = rule.RecommendationPolicy,
            AutomationPolicy = rule.AutomationPolicy,
            Source = source,
            Error = error,
            LastTargetUpdatedUtc = target.LastUpdatedUtc,
        };
    }

    private static void TryAttach(ManualLogSource? log, bool force)
    {
        lock (SyncRoot)
        {
            if (!force && DateTime.UtcNow - _lastAttachAttemptUtc < RetryInterval) return;
            _lastAttachAttemptUtc = DateTime.UtcNow;
        }

        var patchedNow = new List<string>();
        var missing = new List<string>();
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.special-business-context");

            PatchMethod(_harmony, IncomeControllerYuumaTypeName, "SetTargetTag", 3, nameof(OnYuumaTargetTagSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerYuumaTypeName, "SetContext", 6, nameof(OnYuumaContextSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerYuumaTypeName, "SetTargetProgress", 1, nameof(OnYuumaTargetProgressSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerKoishiTypeName, "SetTargetTag", 2, nameof(OnKoishiTargetTagSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerKoishiTypeName, "SetTargetTagTime", 1, nameof(OnKoishiTargetTagTimeSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerKoishiTypeName, "SetTargetTagTimeImmediately", 1, nameof(OnKoishiTargetTagTimeSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerKoishiTypeName, "SetTargetTime", 1, nameof(OnKoishiTargetTimeSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerKoishiTypeName, "SetContext", 4, nameof(OnKoishiContextSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerKoishiTypeName, "SetTargetProgress", 1, nameof(OnKoishiTargetProgressSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerKoishiTypeName, "IntoShieldMode", 2, nameof(OnKoishiShieldModeChanged), patchedNow, missing);
            PatchKoishiClueTagGenerationMethod(_harmony, patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerYuyukoTypeName, "SetContext", 4, nameof(OnYuyukoContextSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerYuyukoTypeName, "SetTargetProgress", 1, nameof(OnYuyukoTargetProgressSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerYuyukoTypeName, "SetTargetTime", 1, nameof(OnYuyukoTargetTimeSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerChallengeTypeName, "SetTargetFund", 1, nameof(OnChallengeTargetFundSet), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerChallengeTypeName, "UpdateSpellCount", 2, nameof(OnChallengeSpellCountUpdated), patchedNow, missing);
            PatchMethod(_harmony, IncomeControllerMausoleumTypeName, "SetTargetFund", 1, nameof(OnMausoleumTargetFundSet), patchedNow, missing);

            lock (SyncRoot)
            {
                _status = PatchedMethods.Count == 0
                    ? $"waiting: {string.Join(", ", missing.Take(4))}"
                    : missing.Count == 0
                        ? $"patched={PatchedMethods.Count}"
                        : $"patched={PatchedMethods.Count}; missing={string.Join(", ", missing.Take(4))}";
            }

            if (patchedNow.Count > 0)
            {
                log?.LogInfo($"Special business context capture patched: {string.Join(", ", patchedNow)}.");
            }
            else if (force && PatchedMethods.Count == 0)
            {
                log?.LogWarning($"Special business context capture waiting for game types: {string.Join(", ", missing.Take(4))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _status = $"error: {ex.Message}";
            }

            log?.LogWarning($"Special business context capture failed: {ex.Message}");
        }
    }

    private static void PatchMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        string postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = RuntimeReflectionUtility.FindType(typeName);
        var target = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
        var postfix = typeof(RuntimeSpecialBusinessContextService).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || postfix == null)
        {
            missing.Add(key);
            return;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        lock (SyncRoot)
        {
            PatchedMethods.Add(key);
        }

        patchedNow.Add(key);
    }

    private static void PatchKoishiClueTagGenerationMethod(
        Harmony harmony,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(KoishiClueGenerationPatchKey)) return;
        }

        var target = FindKoishiClueTagGenerationMethod();
        var postfix = typeof(RuntimeSpecialBusinessContextService).GetMethod(nameof(OnKoishiClueTagsGenerated), BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || postfix == null)
        {
            missing.Add(KoishiClueGenerationPatchKey);
            LogKoishiClueGenerationPatchFailure();
            return;
        }

        harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        lock (SyncRoot)
        {
            PatchedMethods.Add(KoishiClueGenerationPatchKey);
        }

        patchedNow.Add(KoishiClueGenerationPatchKey);
        LogKoishiClueGenerationPatchSuccess(target);
    }

    private static MethodInfo? FindKoishiClueTagGenerationMethod()
    {
        return GetKoishiDisplayClassMethods()
            .Where(method => string.Equals(method.Name, KoishiClueGenerationRuntimeMethodName, StringComparison.Ordinal))
            .Where(method => !method.IsStatic)
            .Where(method => method.GetParameters().Length == 0)
            .Where(method => method.ReturnType == typeof(void))
            .OrderBy(method => method.DeclaringType?.FullName ?? "")
            .FirstOrDefault();
    }

    private static MethodInfo[] GetKoishiDisplayClassMethods()
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(GetTypesSafely)
            .Where(IsKoishiDisplayClass13Type)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .ToArray();
    }

    private static bool IsKoishiDisplayClass13Type(Type type)
    {
        var fullName = type.FullName ?? "";
        return fullName.Contains("DLC2_KoishiBossData", StringComparison.Ordinal)
            && type.Name.Contains("DisplayClass13_0", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> GetTypesSafely(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static void OnYuumaTargetTagSet(object? __0, object? __1)
    {
        UpdateFoodTarget("yuuma", CleanText(__0), CleanText(__1));
    }

    private static void OnYuumaContextSet(object? __0, object? __1, object? __2)
    {
        UpdateProgressContext("yuuma", CleanText(__0), __1, __2, phase: "");
    }

    private static void OnYuumaTargetProgressSet(object? __0)
    {
        UpdateTargetValue("yuuma", __0);
    }

    private static void OnKoishiTargetTagSet(object? __0)
    {
        UpdateFoodTarget("koishi", CleanText(__0));
    }

    private static void OnKoishiContextSet(object? __0, object? __1, object? __2, object? __3)
    {
        UpdateProgressContext("koishi", CleanText(__0), __1, __2, CleanText(__3));
    }

    private static void OnKoishiTargetProgressSet(object? __0)
    {
        UpdateTargetValue("koishi", __0);
    }

    private static void OnKoishiTargetTimeSet(object? __0)
    {
        UpdateTargetTime("koishi", __0);
    }

    private static void OnKoishiTargetTagTimeSet(object? __0)
    {
        UpdateTargetTagTime("koishi", __0);
    }

    private static void OnKoishiShieldModeChanged(object? __0, object? __1)
    {
        var broken = RuntimeReflectionUtility.ToBool(__0);
        var recover = RuntimeReflectionUtility.ToBool(__1);
        lock (SyncRoot)
        {
            _targetKind = "koishi";
            _koishiShieldBroken = recover ? false : broken;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = recover ? "koishi shield recovered" : $"koishi shield broken={broken}";
            _changeVersion++;
            LogWackyTargetStateLocked("koishi-shield");
        }
    }

    private static void OnKoishiClueTagsGenerated(object? __instance, object[] __args)
    {
        if (!string.Equals(CurrentChallengeType, SpecialBusinessChallengeTypes.WackyCookingCompetition, StringComparison.Ordinal)) return;

        var context = ResolveKoishiGenerationContext(__instance, __args);
        if (context == null)
        {
            LogKoishiGeneratedClues(
                "failed",
                "DisplayClass13_0 context not found",
                __instance,
                __args,
                Array.Empty<int>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                int.MinValue,
                int.MinValue,
                int.MinValue,
                int.MinValue);
            return;
        }

        if (!TryReadKoishiGeneratedClues(
            context,
            out var foodPreferenceTags,
            out var foodHateTags,
            out var beveragePreferenceTags,
            out var tagIds,
            out var likeFoodTagNum,
            out var hateFoodTagNum,
            out var likeBeverageTagNum,
            out var tagCount,
            out var failureReason))
        {
            LogKoishiGeneratedClues(
                "failed",
                failureReason,
                context,
                __args,
                Array.Empty<int>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                likeFoodTagNum,
                hateFoodTagNum,
                likeBeverageTagNum,
                tagCount);
            return;
        }

        UpdateKoishiClueTags(foodPreferenceTags, foodHateTags, beveragePreferenceTags);
        LogKoishiGeneratedClues(
            "captured",
            "",
            context,
            __args,
            tagIds,
            foodPreferenceTags,
            foodHateTags,
            beveragePreferenceTags,
            likeFoodTagNum,
            hateFoodTagNum,
            likeBeverageTagNum,
            tagCount);
    }

    private static object? ResolveKoishiGenerationContext(object? instance, IReadOnlyList<object?> args)
    {
        if (IsKoishiGenerationContext(instance)) return instance;
        foreach (var arg in args)
        {
            if (IsKoishiGenerationContext(arg)) return arg;
        }

        return null;
    }

    private static bool IsKoishiGenerationContext(object? value)
    {
        var fullName = value?.GetType().FullName ?? "";
        return fullName.Contains("DLC2_KoishiBossData", StringComparison.Ordinal)
            && fullName.Contains("DisplayClass13_0", StringComparison.Ordinal);
    }

    private static bool TryReadKoishiGeneratedClues(
        object __instance,
        out string[] foodPreferenceTags,
        out string[] foodHateTags,
        out string[] beveragePreferenceTags,
        out int[] tagIds,
        out int likeFoodTagNum,
        out int hateFoodTagNum,
        out int likeBeverageTagNum,
        out int tagCount,
        out string failureReason)
    {
        foodPreferenceTags = Array.Empty<string>();
        foodHateTags = Array.Empty<string>();
        beveragePreferenceTags = Array.Empty<string>();
        tagIds = Array.Empty<int>();
        likeFoodTagNum = int.MinValue;
        hateFoodTagNum = int.MinValue;
        likeBeverageTagNum = int.MinValue;
        tagCount = int.MinValue;
        failureReason = "";

        likeFoodTagNum = RuntimeReflectionUtility.ToInt(ReadMemberValue(__instance, "likeFoodTagNum"), int.MinValue);
        hateFoodTagNum = RuntimeReflectionUtility.ToInt(ReadMemberValue(__instance, "hateFoodTagNum"), int.MinValue);
        likeBeverageTagNum = RuntimeReflectionUtility.ToInt(ReadMemberValue(__instance, "likeBevTagNum"), int.MinValue);
        if (likeFoodTagNum < 0 || hateFoodTagNum < 0 || likeBeverageTagNum < 0)
        {
            failureReason = "tag group counts not found";
            return false;
        }

        var koishiTags = ReadMemberValue(__instance, "koishiTag");
        if (koishiTags == null)
        {
            failureReason = "koishiTag not found";
            return false;
        }

        if (!TryReadIntList(koishiTags, out tagIds, out tagCount))
        {
            failureReason = "koishiTag values not readable";
            return false;
        }

        var expectedCount = likeFoodTagNum + hateFoodTagNum + likeBeverageTagNum;
        if (expectedCount <= 0)
        {
            failureReason = "tag group counts are empty";
            return false;
        }

        if (tagIds.Length < expectedCount)
        {
            failureReason = $"koishiTag count {tagIds.Length} is less than expected {expectedCount}";
            return false;
        }

        foodPreferenceTags = ResolveKoishiClueTags("food-like", tagIds.Take(likeFoodTagNum));
        foodHateTags = ResolveKoishiClueTags("food-hate", tagIds.Skip(likeFoodTagNum).Take(hateFoodTagNum));
        beveragePreferenceTags = ResolveKoishiClueTags("beverage-like", tagIds.Skip(likeFoodTagNum + hateFoodTagNum).Take(likeBeverageTagNum));
        if (foodPreferenceTags.Length == 0 || beveragePreferenceTags.Length == 0)
        {
            failureReason = "resolved clue tags are incomplete";
            return false;
        }

        return true;
    }

    private static void OnYuyukoContextSet(object? __0, object? __1, object? __2, object? __3)
    {
        UpdateProgressContext("yuyuko", CleanText(__0), __1, __2, CleanText(__3));
    }

    private static void OnYuyukoTargetProgressSet(object? __0)
    {
        UpdateTargetValue("yuyuko", __0);
    }

    private static void OnYuyukoTargetTimeSet(object? __0)
    {
        UpdateTargetTime("yuyuko", __0);
    }

    private static void OnChallengeTargetFundSet(object? __0)
    {
        UpdateTargetFund("challenge", __0, "目标营业额");
    }

    private static void OnChallengeSpellCountUpdated(object? __0, object? __1)
    {
        var current = RuntimeReflectionUtility.ToInt(__0, int.MinValue);
        var total = RuntimeReflectionUtility.ToInt(__1, int.MinValue);
        if (current < 0 || total < 0) return;

        lock (SyncRoot)
        {
            _targetKind = "challenge";
            _currentSpellCount = current;
            _targetSpellCount = total;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"challenge spell count={current}/{total}";
            _changeVersion++;
        }
    }

    private static void OnMausoleumTargetFundSet(object? __0)
    {
        UpdateTargetFund("mausoleum", __0, "目标营业额");
    }

    private static void UpdateTargetFund(string kind, object? value, string label)
    {
        var targetFund = RuntimeReflectionUtility.ToInt(value, int.MinValue);
        if (targetFund < 0) return;

        lock (SyncRoot)
        {
            _targetKind = kind;
            _foodTargetTags = Array.Empty<string>();
            _targetFund = targetFund;
            _targetLabel = label;
            _targetTimeProgress = null;
            _targetTagTimeProgress = null;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"{kind} target fund={targetFund}";
            _changeVersion++;
            LogWackyTargetStateLocked("target-fund");
        }
    }

    private static void UpdateFoodTarget(string kind, params string[] tags)
    {
        var normalized = tags
            .Select(CleanText)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0) return;

        lock (SyncRoot)
        {
            _targetKind = kind;
            _foodTargetTags = normalized;
            _targetFund = null;
            _targetLabel = "目标料理 Tag";
            _targetTagTimeProgress = null;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"{kind} food tags={string.Join(",", normalized)}";
            _changeVersion++;
            LogWackyTargetStateLocked("target-tags");
        }
    }

    private static void UpdateProgressContext(string kind, string label, object? currentValue, object? maxValue, string phase)
    {
        var current = RuntimeReflectionUtility.ToInt(currentValue, int.MinValue);
        var max = RuntimeReflectionUtility.ToInt(maxValue, int.MinValue);

        lock (SyncRoot)
        {
            var previousKind = _targetKind;
            var previousPhase = _phase;
            var resetTargetValue = !string.Equals(_targetKind, kind, StringComparison.Ordinal)
                || !string.Equals(_phase, phase, StringComparison.Ordinal);
            _targetKind = kind;
            _targetLabel = label;
            _currentValue = current == int.MinValue ? null : current;
            _maxValue = max == int.MinValue ? null : max;
            _phase = phase;
            if (resetTargetValue)
            {
                _targetValue = null;
                ResetTransientStateForContextLocked(previousKind, previousPhase, kind, phase);
            }
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"{kind} context={label}; progress={_currentValue?.ToString() ?? ""}/{_maxValue?.ToString() ?? ""}; phase={phase}";
            _changeVersion++;
            LogWackyTargetStateLocked("context");
            LogYuyukoTargetStateLocked("context");
        }
    }

    private static void ResetTransientStateForContextLocked(string previousKind, string previousPhase, string kind, string phase)
    {
        if (string.Equals(kind, "koishi", StringComparison.Ordinal))
        {
            _foodTargetTags = Array.Empty<string>();
            _targetTimeProgress = null;
            _targetTagTimeProgress = null;

            if (!IsKoishiPhaseThreeLocked(phase))
            {
                _koishiShieldBroken = null;
                _koishiFoodPreferenceTags = Array.Empty<string>();
                _koishiFoodHateTags = Array.Empty<string>();
                _koishiBeveragePreferenceTags = Array.Empty<string>();
            }

            return;
        }

        if (!string.Equals(previousKind, kind, StringComparison.Ordinal)
            || !string.Equals(previousPhase, phase, StringComparison.Ordinal))
        {
            _foodTargetTags = Array.Empty<string>();
            _targetTimeProgress = null;
            _targetTagTimeProgress = null;
            _koishiShieldBroken = null;
            _koishiFoodPreferenceTags = Array.Empty<string>();
            _koishiFoodHateTags = Array.Empty<string>();
            _koishiBeveragePreferenceTags = Array.Empty<string>();
        }
    }

    private static bool IsKoishiPhaseWithoutFoodTargetLocked(string phase)
    {
        return string.Equals(phase.Trim(), "Phase1", StringComparison.Ordinal);
    }

    private static bool IsKoishiPhaseThreeLocked(string phase)
    {
        return string.Equals(phase.Trim(), "Phase3", StringComparison.Ordinal);
    }

    private static void UpdateTargetValue(string kind, object? value)
    {
        var target = RuntimeReflectionUtility.ToInt(value, int.MinValue);
        if (target == int.MinValue) return;

        lock (SyncRoot)
        {
            _targetKind = kind;
            _targetValue = target;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"{kind} target progress={target}";
            _changeVersion++;
            LogWackyTargetStateLocked("target-progress");
            LogYuyukoTargetStateLocked("target-progress");
        }
    }

    private static void UpdateTargetTime(string kind, object? value)
    {
        var progress = ToDouble(value, double.NaN);
        if (double.IsNaN(progress)) return;

        lock (SyncRoot)
        {
            _targetKind = kind;
            _targetTimeProgress = ClampProgress(progress);
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"{kind} time={FormatProgress(_targetTimeProgress)}";
            _changeVersion++;
            LogWackyTargetStateLocked("target-time", bucketByTime: true);
            LogYuyukoTargetStateLocked("target-time", bucketByTime: true);
        }
    }

    private static void UpdateTargetTagTime(string kind, object? value)
    {
        var progress = ToDouble(value, double.NaN);
        if (double.IsNaN(progress)) return;

        lock (SyncRoot)
        {
            _targetKind = kind;
            _targetTagTimeProgress = ClampProgress(progress);
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"{kind} tag time={FormatProgress(_targetTagTimeProgress)}";
            _changeVersion++;
            LogWackyTargetStateLocked("target-tag-time", bucketByTagTime: true);
        }
    }

    private static void UpdateKoishiClueTags(
        IReadOnlyList<string> foodPreferenceTags,
        IReadOnlyList<string> foodHateTags,
        IReadOnlyList<string> beveragePreferenceTags)
    {
        lock (SyncRoot)
        {
            _targetKind = "koishi";
            _koishiFoodPreferenceTags = NormalizeTagCollection(foodPreferenceTags);
            _koishiFoodHateTags = NormalizeTagCollection(foodHateTags);
            _koishiBeveragePreferenceTags = NormalizeTagCollection(beveragePreferenceTags);
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"koishi clues foodLike={SpecialBusinessDiagnostics.FormatTags(_koishiFoodPreferenceTags)}; foodHate={SpecialBusinessDiagnostics.FormatTags(_koishiFoodHateTags)}; beverageLike={SpecialBusinessDiagnostics.FormatTags(_koishiBeveragePreferenceTags)}";
            _changeVersion++;
            LogWackyTargetStateLocked("koishi-clues-generated");
        }
    }

    private static string[] NormalizeTagCollection(IEnumerable<string> source)
    {
        return source
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryReadIntList(object collection, out int[] values, out int count)
    {
        values = Array.Empty<int>();
        count = RuntimeReflectionUtility.ToInt(ReadMemberValue(collection, "Count") ?? ReadMemberValue(collection, "Length"), int.MinValue);
        var items = new List<int>();
        if (count > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var value = RuntimeReflectionUtility.InvokeMethod(collection, "get_Item", index);
                var item = RuntimeReflectionUtility.ToInt(value, int.MinValue);
                if (item == int.MinValue) return false;
                items.Add(item);
            }

            values = items.ToArray();
            return values.Length > 0;
        }

        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(collection))
        {
            var value = RuntimeReflectionUtility.ToInt(item, int.MinValue);
            if (value == int.MinValue) return false;
            items.Add(value);
        }

        values = items.ToArray();
        count = values.Length;
        return values.Length > 0;
    }

    private static string[] ResolveKoishiClueTags(string kind, IEnumerable<int> tagIds)
    {
        return tagIds
            .Select(tagId => ResolveKoishiClueTag(kind, tagId))
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveKoishiClueTag(string kind, int tagId)
    {
        var languageType = RuntimeReflectionUtility.FindType(DataBaseLanguageTypeName);
        if (languageType == null) return "";

        var methodName = string.Equals(kind, "beverage-like", StringComparison.Ordinal)
            ? "GetBeverageTag"
            : "GetFoodTag";
        var tag = CleanText(RuntimeReflectionUtility.InvokeStaticMethod(languageType, methodName, tagId));
        if (tag.Length == 0) return "";
        return string.Equals(kind, "beverage-like", StringComparison.Ordinal)
            ? tag
            : NormalizeFoodTag(tag);
    }

    private static string NormalizeFoodTag(string tag)
    {
        var trimmed = tag.Trim();
        if (trimmed.Length == 0) return "";
        return FoodTags.NormalizeName(trimmed) ?? trimmed;
    }

    private static void LogKoishiGeneratedClues(
        string status,
        string reason,
        object? instance,
        IReadOnlyList<object?> args,
        IReadOnlyList<int> tagIds,
        IReadOnlyList<string> foodPreferenceTags,
        IReadOnlyList<string> foodHateTags,
        IReadOnlyList<string> beveragePreferenceTags,
        int likeFoodTagNum,
        int hateFoodTagNum,
        int likeBeverageTagNum,
        int tagCount)
    {
        SpecialBusinessDiagnostics.AppendWackySnapshot(
            "Wacky Cooking Koishi Clue Generated",
            new[]
            {
                $"status: {status}",
                $"reason: {reason}",
                $"phase: {_phase}",
                $"instanceType: {instance?.GetType().FullName ?? ""}",
                $"args: {DescribeArguments(args)}",
                $"likeFoodTagNum: {FormatOptionalInt(likeFoodTagNum)}",
                $"hateFoodTagNum: {FormatOptionalInt(hateFoodTagNum)}",
                $"likeBeverageTagNum: {FormatOptionalInt(likeBeverageTagNum)}",
                $"koishiTagCount: {FormatOptionalInt(tagCount)}",
                $"tagIds: {SpecialBusinessDiagnostics.FormatIds(tagIds)}",
                $"foodPreferenceTags: {SpecialBusinessDiagnostics.FormatTags(foodPreferenceTags)}",
                $"foodHateTags: {SpecialBusinessDiagnostics.FormatTags(foodHateTags)}",
                $"beveragePreferenceTags: {SpecialBusinessDiagnostics.FormatTags(beveragePreferenceTags)}",
                $"fields: {DescribeInstanceFields(instance)}",
            },
            string.Equals(status, "captured", StringComparison.Ordinal)
                ? null
                : $"wacky-koishi-clue-generated-failed|{reason}|{instance?.GetType().FullName ?? "none"}");
    }

    private static void LogKoishiClueGenerationPatchSuccess(MethodInfo target)
    {
        SpecialBusinessDiagnostics.AppendWackySnapshot(
            "Wacky Cooking Koishi Clue Generation Patch",
            new[]
            {
                "status: patched",
                $"target: {DescribeMethodSignature(target)}",
                $"candidateWindow: {DescribeKoishiGenerationCandidateWindow(target)}",
            },
            "wacky-koishi-clue-generation-patched");
    }

    private static void LogKoishiClueGenerationPatchFailure()
    {
        SpecialBusinessDiagnostics.AppendWackySnapshot(
            "Wacky Cooking Koishi Clue Generation Patch Failed",
            new[]
            {
                $"reason: runtime method not found: {KoishiClueGenerationRuntimeMethodName}",
                $"candidates: {DescribeKoishiGenerationMethodCandidates()}",
            },
            "wacky-koishi-clue-generation-patch-failed");
    }

    private static string FormatOptionalInt(int value)
    {
        return value == int.MinValue ? "" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string DescribeKoishiGenerationMethodCandidates()
    {
        var candidates = GetKoishiDisplayClassMethods()
            .Select(DescribeMethodSignature)
            .ToArray();
        return candidates.Length == 0 ? "(none)" : string.Join("; ", candidates);
    }

    private static string DescribeKoishiGenerationCandidateWindow(MethodInfo target)
    {
        var methods = GetKoishiDisplayClassMethods();
        if (methods.Length == 0) return "(none)";

        var targetIndex = Array.FindIndex(methods, method => IsSameMethodSignature(method, target));
        if (targetIndex < 0) return DescribeMethodSignature(target);

        var start = Math.Max(0, targetIndex - 3);
        var count = Math.Min(methods.Length - start, 7);
        return string.Join("; ", methods.Skip(start).Take(count).Select(DescribeMethodSignature));
    }

    private static bool IsSameMethodSignature(MethodInfo left, MethodInfo right)
    {
        if (!string.Equals(left.DeclaringType?.FullName, right.DeclaringType?.FullName, StringComparison.Ordinal)) return false;
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)) return false;
        var leftParameters = left.GetParameters();
        var rightParameters = right.GetParameters();
        if (leftParameters.Length != rightParameters.Length) return false;
        for (var i = 0; i < leftParameters.Length; i++)
        {
            if (leftParameters[i].ParameterType != rightParameters[i].ParameterType) return false;
        }

        return left.ReturnType == right.ReturnType;
    }

    private static string DescribeMethodSignature(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Select(parameter => $"{parameter.ParameterType.Name} {parameter.Name}")
            .ToArray();
        var kind = method.IsStatic ? "static" : "instance";
        return $"{method.DeclaringType?.FullName}.{method.Name}({string.Join(", ", parameters)}) [{kind}]";
    }

    private static string DescribeArguments(IReadOnlyList<object?> args)
    {
        if (args.Count == 0) return "(none)";
        return string.Join("; ", args.Select((arg, index) => $"{index}:{arg?.GetType().FullName ?? "null"}"));
    }

    private static string DescribeInstanceFields(object? instance)
    {
        if (instance == null) return "(none)";
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var fields = new List<string>();
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(flags))
            {
                fields.Add($"{field.Name}:{field.FieldType.Name}");
                if (fields.Count >= 48) return string.Join("; ", fields);
            }
        }

        return fields.Count == 0 ? "(none)" : string.Join("; ", fields);
    }

    private static object? ReadMemberValue(object? instance, string name)
    {
        return RuntimeReflectionUtility.GetMemberValue(instance, name);
    }

    private static void LogWackyTargetStateLocked(
        string eventName,
        bool bucketByTime = false,
        bool bucketByTagTime = false)
    {
        if (!string.Equals(_targetKind, "koishi", StringComparison.Ordinal)) return;

        var lines = new[]
        {
            $"event: {eventName}",
            $"targetKind: {_targetKind}",
            $"foodTargetTags: {SpecialBusinessDiagnostics.FormatTags(_foodTargetTags)}",
            $"targetFund: {_targetFund?.ToString() ?? ""}",
            $"targetLabel: {_targetLabel}",
            $"phase: {_phase}",
            $"currentValue: {_currentValue?.ToString() ?? ""}",
            $"maxValue: {_maxValue?.ToString() ?? ""}",
            $"targetValue: {_targetValue?.ToString() ?? ""}",
            $"targetTimeProgress: {FormatProgress(_targetTimeProgress)}",
            $"targetTagTimeProgress: {FormatProgress(_targetTagTimeProgress)}",
            $"koishiShieldBroken: {_koishiShieldBroken?.ToString() ?? ""}",
            $"koishiFoodPreferenceTags: {SpecialBusinessDiagnostics.FormatTags(_koishiFoodPreferenceTags)}",
            $"koishiFoodHateTags: {SpecialBusinessDiagnostics.FormatTags(_koishiFoodHateTags)}",
            $"koishiBeveragePreferenceTags: {SpecialBusinessDiagnostics.FormatTags(_koishiBeveragePreferenceTags)}",
            $"lastAction: {_lastAction}",
            $"changeVersion: {_changeVersion}",
        };

        if (bucketByTagTime)
        {
            SpecialBusinessDiagnostics.AppendWackyProgressSnapshot(
                $"hud-tag-time|{string.Join(",", _foodTargetTags)}|{_phase}",
                _targetTagTimeProgress,
                "Wacky Cooking HUD Target Tag Time",
                lines);
            return;
        }

        if (bucketByTime)
        {
            SpecialBusinessDiagnostics.AppendWackyProgressSnapshot(
                $"hud-time|{string.Join(",", _foodTargetTags)}|{_phase}",
                _targetTimeProgress,
                "Wacky Cooking HUD Target Time",
                lines);
            return;
        }

        SpecialBusinessDiagnostics.AppendWackySnapshot(
            "Wacky Cooking HUD Target Updated",
            lines,
            $"hud|{eventName}|{string.Join(",", _foodTargetTags)}|{_phase}|{_currentValue}|{_maxValue}|{_targetValue}|{_targetFund}");
    }

    private static void LogYuyukoTargetStateLocked(
        string eventName,
        bool bucketByTime = false)
    {
        if (!string.Equals(_targetKind, "yuyuko", StringComparison.Ordinal)) return;

        var lines = new[]
        {
            $"event: {eventName}",
            $"targetKind: {_targetKind}",
            $"targetLabel: {_targetLabel}",
            $"phase: {_phase}",
            $"currentValue: {_currentValue?.ToString() ?? ""}",
            $"maxValue: {_maxValue?.ToString() ?? ""}",
            $"targetValue: {_targetValue?.ToString() ?? ""}",
            $"targetTimeProgress: {FormatProgress(_targetTimeProgress)}",
            "phase3Evaluation: P3 automation treats Good/ExGood as the progress threshold; Story P3 callback details are logged for native verification.",
            $"lastAction: {_lastAction}",
            $"changeVersion: {_changeVersion}",
        };

        if (bucketByTime)
        {
            SpecialBusinessDiagnostics.AppendYuyukoProgressSnapshot(
                $"hud-time|{_phase}",
                _targetTimeProgress,
                "Yuyuko Challenge HUD Target Time",
                lines);
            return;
        }

        SpecialBusinessDiagnostics.AppendYuyukoSnapshot(
            "Yuyuko Challenge HUD Target Updated",
            lines,
            $"hud|{eventName}|{_phase}|{_currentValue}|{_maxValue}|{_targetValue}");
    }

    private static ChallengeTypeState ReadChallengeTypeState(out string? error)
    {
        var rawChallengeType = ReadRawChallengeType(out var rawError);
        var difficultyMode = ReadDifficultyMode(out var difficultyError);
        var effectiveChallengeType = rawChallengeType;
        var variantSource = "";

        lock (SyncRoot)
        {
            if (!IsYuyukoChallengeType(rawChallengeType))
            {
                _yuyukoRetakeEvidenceSource = "";
            }
            else if (string.Equals(rawChallengeType, SpecialBusinessChallengeTypes.RetakeYuyuko, StringComparison.Ordinal))
            {
                effectiveChallengeType = SpecialBusinessChallengeTypes.RetakeYuyuko;
                variantSource = "raw ChallengeMode";
                _yuyukoRetakeEvidenceSource = variantSource;
            }
            else if (IsMeaningfulDifficultyMode(difficultyMode))
            {
                effectiveChallengeType = SpecialBusinessChallengeTypes.RetakeYuyuko;
                variantSource = $"DifficultyMode={difficultyMode}";
                _yuyukoRetakeEvidenceSource = variantSource;
            }
            else if (!string.IsNullOrWhiteSpace(_yuyukoRetakeEvidenceSource))
            {
                effectiveChallengeType = SpecialBusinessChallengeTypes.RetakeYuyuko;
                variantSource = _yuyukoRetakeEvidenceSource;
            }
            else
            {
                effectiveChallengeType = SpecialBusinessChallengeTypes.StoryYuyuko;
                variantSource = $"raw {SpecialBusinessChallengeTypes.StoryYuyuko}";
            }
        }

        error = string.Join("; ", new[] { rawError, difficultyError }.Where(item => !string.IsNullOrWhiteSpace(item)));
        if (string.IsNullOrWhiteSpace(error)) error = null;
        return new ChallengeTypeState(rawChallengeType, effectiveChallengeType, difficultyMode, variantSource);
    }

    private static string ReadRawChallengeType(out string? error)
    {
        error = null;
        try
        {
            var type = RuntimeReflectionUtility.FindType(NightSceneDirectorTypeName);
            if (type == null)
            {
                error = "NightSceneDirector type not found";
                return SpecialBusinessChallengeTypes.NotChallenge;
            }

            var value = RuntimeReflectionUtility.GetStaticMemberValue(type, "ChallengeMode");
            var text = NormalizeChallengeTypeText(CleanText(value));
            return text.Length == 0 ? SpecialBusinessChallengeTypes.NotChallenge : text;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return SpecialBusinessChallengeTypes.NotChallenge;
        }
    }

    private static string ReadDifficultyMode(out string? error)
    {
        error = null;
        try
        {
            var type = RuntimeReflectionUtility.FindType(NightSceneDirectorTypeName);
            if (type == null)
            {
                error = "NightSceneDirector type not found";
                return NoDifficultyMode;
            }

            var value = RuntimeReflectionUtility.GetStaticMemberValue(type, "DifficultyMode");
            var text = NormalizeDifficultyModeText(CleanText(value));
            return text.Length == 0 ? NoDifficultyMode : text;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return NoDifficultyMode;
        }
    }

    private static void SetYuyukoRetakeEvidenceLocked(string source)
    {
        var normalizedSource = CleanText(source);
        if (normalizedSource.Length == 0) normalizedSource = "runtime evidence";
        if (string.Equals(_yuyukoRetakeEvidenceSource, normalizedSource, StringComparison.Ordinal)) return;

        _yuyukoRetakeEvidenceSource = normalizedSource;
        _lastAction = $"yuyuko retake evidence={normalizedSource}";
        _changeVersion++;
    }

    private static bool IsYuyukoChallengeType(string challengeType)
    {
        return string.Equals(challengeType, SpecialBusinessChallengeTypes.StoryYuyuko, StringComparison.Ordinal)
            || string.Equals(challengeType, SpecialBusinessChallengeTypes.RetakeYuyuko, StringComparison.Ordinal);
    }

    private static bool IsMeaningfulDifficultyMode(string difficultyMode)
    {
        return !string.IsNullOrWhiteSpace(difficultyMode)
            && !string.Equals(difficultyMode, NoDifficultyMode, StringComparison.Ordinal);
    }

    private static string NormalizeChallengeTypeText(string value)
    {
        return value switch
        {
            "0" => SpecialBusinessChallengeTypes.NotChallenge,
            "3" => SpecialBusinessChallengeTypes.StoryYuyuko,
            "4" => SpecialBusinessChallengeTypes.RetakeYuyuko,
            _ => value,
        };
    }

    private static string NormalizeDifficultyModeText(string value)
    {
        return value switch
        {
            "" => NoDifficultyMode,
            "0" => NoDifficultyMode,
            "1" => "Easy",
            "2" => "Normal",
            "3" => "Hard",
            "4" => "Lunatic",
            _ => value,
        };
    }

    private static SpecialBusinessTarget ReadTargetForChallenge(string challengeType)
    {
        var expectedKind = challengeType switch
        {
            "Story_BloodPondHell" => "yuuma",
            SpecialBusinessChallengeTypes.WackyCookingCompetition => "koishi",
            "Story_Basic" => "challenge",
            "Story_Advanced" => "challenge",
            SpecialBusinessChallengeTypes.StoryYuyuko => "yuyuko",
            SpecialBusinessChallengeTypes.RetakeYuyuko => "yuyuko",
            "Story_Seiga_TempleCuisineCompetition" => "mausoleum",
            "Story_Futo_TempleCuisineCompetition" => "mausoleum",
            "Story_Tochiko_TempleCuisineCompetition" => "mausoleum",
            _ => "",
        };

        if (expectedKind.Length == 0) return SpecialBusinessTarget.Empty;

        lock (SyncRoot)
        {
            if (!string.Equals(_targetKind, expectedKind, StringComparison.Ordinal))
            {
                return SpecialBusinessTarget.Empty;
            }

            return new SpecialBusinessTarget(
                NormalizeFoodTargetTagsLocked(),
                Array.Empty<string>(),
                _targetFund,
                _targetLabel,
                _phase,
                _currentValue,
                _maxValue,
                _targetValue,
                _targetTimeProgress,
                _targetTagTimeProgress,
                _koishiShieldBroken,
                _koishiFoodPreferenceTags,
                _koishiFoodHateTags,
                _koishiBeveragePreferenceTags,
                _currentSpellCount,
                _targetSpellCount,
                _lastTargetUpdatedUtc,
                _targetKind);
        }
    }

    private static SpecialBusinessContextRule GetRule(string challengeType, bool active)
    {
        return SpecialBusinessContextRuleRegistry.GetRule(challengeType, active);
    }

    private static bool IsActiveChallenge(string challengeType)
    {
        return !string.IsNullOrWhiteSpace(challengeType)
            && !string.Equals(challengeType, SpecialBusinessChallengeTypes.NotChallenge, StringComparison.Ordinal);
    }

    private static string BuildSource(ChallengeTypeState challengeState, SpecialBusinessTarget target)
    {
        var source = $"ChallengeMode={challengeState.EffectiveChallengeType}; RawChallengeMode={challengeState.RawChallengeType}; DifficultyMode={challengeState.DifficultyMode}; VariantSource={challengeState.VariantSource}; Capture={Status}";
        if (target.Source.Length > 0)
        {
            source += $"; TargetSource={target.Source}";
        }

        return source;
    }

    private readonly record struct ChallengeTypeState(
        string RawChallengeType,
        string EffectiveChallengeType,
        string DifficultyMode,
        string VariantSource);

    private static double ToDouble(object? value, double fallback)
    {
        if (value == null) return fallback;
        if (value is double doubleValue) return doubleValue;
        if (value is float floatValue) return floatValue;
        try
        {
            if (value is IConvertible convertible)
            {
                return convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
            }

            return double.TryParse(
                value.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static double ClampProgress(double value)
    {
        if (double.IsNaN(value)) return value;
        if (value < 0) return 0;
        if (value > 1) return 1;
        return Math.Round(value, 4);
    }

    private static string FormatProgress(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            : "";
    }

    private static string CleanText(object? value)
    {
        return value?.ToString()?.Trim() ?? "";
    }

    private sealed record SpecialBusinessTarget(
        IReadOnlyList<string> FoodTargetTags,
        IReadOnlyList<string> BeverageTargetTags,
        int? TargetFund,
        string TargetLabel,
        string Phase,
        int? CurrentValue,
        int? MaxValue,
        int? TargetValue,
        double? TargetTimeProgress,
        double? TargetTagTimeProgress,
        bool? WackyKoishiShieldBroken,
        IReadOnlyList<string> WackyKoishiFoodPreferenceTags,
        IReadOnlyList<string> WackyKoishiFoodHateTags,
        IReadOnlyList<string> WackyKoishiBeveragePreferenceTags,
        int? CurrentSpellCount,
        int? TargetSpellCount,
        DateTime? LastUpdatedUtc,
        string Source)
    {
        public static SpecialBusinessTarget Empty { get; } = new(
            Array.Empty<string>(),
            Array.Empty<string>(),
            null,
            "",
            "",
            null,
            null,
            null,
            null,
            null,
            null,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            null,
            null,
            null,
            "");
    }
}
