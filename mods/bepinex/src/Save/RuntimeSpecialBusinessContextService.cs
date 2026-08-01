using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MystiaStewardCompanion.Core;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeSpecialBusinessContextService
{
    private const string NightSceneDirectorTypeName = "NightScene.NightSceneDirector";
    private const string ChallengeTypeNestedName = "ChallengeType";
    private const string ChallengeDisplayNameSource = "NightSceneDirector.ChallengeType.IL2CPPMetadata.InspectorName";
    private const string IncomeControllerYuumaTypeName = "NightScene.UI.HUDUtility.IncomeControllerYuuma";
    private const string IncomeControllerKoishiTypeName = "NightScene.UI.HUDUtility.IncomeControllerKoishi";
    private const string IncomeControllerYuyukoTypeName = "NightScene.UI.HUDUtility.IncomeControllerYuyuko";
    private const string IncomeControllerChallengeTypeName = "NightScene.UI.HUDUtility.IncomeControllerChallenge";
    private const string IncomeControllerMausoleumTypeName = "NightScene.UI.HUDUtility.IncomeControllerMausoleumCuisineCompetition";
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string KoishiClueGenerationPatchKey = "GameData.Profile.DLC2_KoishiBossData.DisplayClass13_0.KoishiClueTagsGenerated";
    private const string KoishiClueGenerationRuntimeMethodName = "Method_Internal_Void_2";
    private const string NoDifficultyMode = "None";
    private const int MaxCaptureFailureDiagnostics = 128;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, ChallengeDisplayNameResolution> ChallengeDisplayNameCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly HashSet<string> CaptureFailureDiagnostics = new(StringComparer.Ordinal);
    private static readonly Queue<string> CaptureFailureDiagnosticOrder = new();
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ChallengeDisplayNameExceptionRetryInterval = TimeSpan.FromSeconds(30);

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
    private static int? _currentAnger;
    private static int? _maxAnger;
    private static int? _targetAnger;
    private static double? _targetTimeProgress;
    private static double? _targetTagTimeProgress;
    private static bool? _koishiShieldBroken;
    private static string[] _koishiFoodPreferenceTags = Array.Empty<string>();
    private static string[] _koishiFoodHateTags = Array.Empty<string>();
    private static string[] _koishiBeveragePreferenceTags = Array.Empty<string>();
    private static int? _currentSpellCount;
    private static int? _targetSpellCount;
    private static DateTime? _lastTargetUpdatedUtc;
    private static string _targetRawChallengeType = "";
    private static long _targetBusinessGeneration;
    private static long _yuumaFoodTargetRevision;
    private static string _yuumaFoodTargetIdentity = "";
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
                    : $"{_targetKind}; foodTags={string.Join(",", _foodTargetTags)}; yuumaTargetRevision={_yuumaFoodTargetRevision}; targetFund={_targetFund?.ToString() ?? ""}; progress={_currentValue?.ToString() ?? ""}/{_maxValue?.ToString() ?? ""}; target={_targetValue?.ToString() ?? ""}; anger={_currentAnger?.ToString() ?? ""}/{_maxAnger?.ToString() ?? ""}; targetAnger={_targetAnger?.ToString() ?? ""}; time={FormatProgress(_targetTimeProgress)}; tagTime={FormatProgress(_targetTagTimeProgress)}; koishiShield={_koishiShieldBroken?.ToString() ?? ""}; koishiFoodLikes={string.Join(",", _koishiFoodPreferenceTags)}; koishiFoodHates={string.Join(",", _koishiFoodHateTags)}; koishiBevLikes={string.Join(",", _koishiBeveragePreferenceTags)}; spells={_currentSpellCount?.ToString() ?? ""}/{_targetSpellCount?.ToString() ?? ""}; phase={_phase}";
                var yuyukoVariant = _yuyukoRetakeEvidenceSource.Length == 0
                    ? ""
                    : $"; yuyukoRetakeEvidence={_yuyukoRetakeEvidenceSource}";
                return $"{_status}; version={_changeVersion}; owner={_targetRawChallengeType}; ownerGeneration={_targetBusinessGeneration}; target={target}; last={_lastAction}{yuyukoVariant}";
            }
        }
    }

    public static string CurrentChallengeType => RuntimeNightBusinessLifecycle.IsActive
        ? ReadChallengeTypeState(out _).EffectiveChallengeType
        : SpecialBusinessChallengeTypes.NotChallenge;

    public static string CurrentRawChallengeType => RuntimeNightBusinessLifecycle.IsActive
        ? ReadRawChallengeType(out _)
        : SpecialBusinessChallengeTypes.NotChallenge;

    public static bool TryGetCurrentChallengeType(out string challengeType, out string? error)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive)
        {
            challengeType = SpecialBusinessChallengeTypes.NotChallenge;
            error = null;
            return true;
        }

        var state = ReadChallengeTypeState(out error);
        challengeType = state.EffectiveChallengeType;
        return state.RawChallengeTypeAvailable;
    }

    internal static bool TryGetActiveYuumaGeneration(out long generation)
    {
        generation = 0;
        if (!RuntimeNightBusinessLifecycle.IsActive) return false;

        var activeGeneration = RuntimeNightBusinessLifecycle.Generation;
        lock (SyncRoot)
        {
            if (activeGeneration <= 0
                || _targetBusinessGeneration != activeGeneration
                || !string.Equals(
                    _targetRawChallengeType,
                    SpecialBusinessChallengeTypes.BloodPondHell,
                    StringComparison.Ordinal)
                || !string.Equals(_targetKind, "yuuma", StringComparison.Ordinal))
            {
                return false;
            }

            generation = activeGeneration;
            return true;
        }
    }

    internal static bool TryGetActiveYuumaFoodTargetState(
        out SpecialFoodTargetPolicy? policy,
        out long revision)
    {
        policy = null;
        revision = 0;
        if (!RuntimeNightBusinessLifecycle.IsActive) return false;

        var generation = RuntimeNightBusinessLifecycle.Generation;
        lock (SyncRoot)
        {
            if (generation <= 0
                || _targetBusinessGeneration != generation
                || !string.Equals(
                    _targetRawChallengeType,
                    SpecialBusinessChallengeTypes.BloodPondHell,
                    StringComparison.Ordinal)
                || !string.Equals(_targetKind, "yuuma", StringComparison.Ordinal)
                || _yuumaFoodTargetRevision <= 0)
            {
                return false;
            }

            var normalized = NormalizeFoodTargetTagsLocked();
            if (normalized.Length != 2) return false;

            policy = SpecialFoodTargetPolicy.CreateActive(
                SpecialBusinessChallengeTypes.BloodPondHell,
                "yuuma",
                generation,
                normalized,
                SpecialFoodTargetMatchMode.All);
            revision = _yuumaFoodTargetRevision;
            return true;
        }
    }

    public static bool IsRetakeYuyukoChallenge => string.Equals(CurrentChallengeType, SpecialBusinessChallengeTypes.RetakeYuyuko, StringComparison.Ordinal);

    public static void MarkYuyukoRetakeEvidence(string source)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

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
        if (!RuntimeNightBusinessLifecycle.IsActive) return false;
        if (!TryReadTargetOwner("koishi", out var rawChallengeType)) return false;

        lock (SyncRoot)
        {
            return TargetContextMatchesLocked(rawChallengeType, "koishi")
                && string.Equals(_phase.Trim(), phase, StringComparison.Ordinal);
        }
    }

    public static bool IsWackyKoishiShieldBroken
    {
        get
        {
            if (!RuntimeNightBusinessLifecycle.IsActive) return false;
            if (!TryReadTargetOwner("koishi", out var rawChallengeType)) return false;

            lock (SyncRoot)
            {
                return TargetContextMatchesLocked(rawChallengeType, "koishi")
                    && _koishiShieldBroken == true;
            }
        }
    }

    public static bool IsActiveYuyukoPhase(string phase)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return false;
        if (!TryReadTargetOwner("yuyuko", out var rawChallengeType)) return false;

        lock (SyncRoot)
        {
            return TargetContextMatchesLocked(rawChallengeType, "yuyuko")
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

    public static bool TryGetActiveSpecialFoodTargetPolicy(out SpecialFoodTargetPolicy? policy)
    {
        policy = null;
        if (!RuntimeNightBusinessLifecycle.IsActive) return false;

        var rawChallengeType = ReadRawChallengeType(out var error);
        if (!string.IsNullOrWhiteSpace(error)) return false;
        var owner = GetExpectedTargetKind(rawChallengeType);
        var matchMode = rawChallengeType switch
        {
            SpecialBusinessChallengeTypes.WackyCookingCompetition => SpecialFoodTargetMatchMode.Any,
            SpecialBusinessChallengeTypes.BloodPondHell => SpecialFoodTargetMatchMode.All,
            _ => (SpecialFoodTargetMatchMode?)null,
        };
        if (!matchMode.HasValue || owner.Length == 0) return false;

        lock (SyncRoot)
        {
            if (!TargetContextMatchesLocked(rawChallengeType, owner)) return false;

            var normalized = NormalizeFoodTargetTagsLocked();
            if (normalized.Length == 0) return false;
            if (string.Equals(rawChallengeType, SpecialBusinessChallengeTypes.BloodPondHell, StringComparison.Ordinal)
                && normalized.Length != 2)
            {
                return false;
            }

            policy = SpecialFoodTargetPolicy.CreateActive(
                rawChallengeType,
                owner,
                _targetBusinessGeneration,
                normalized,
                matchMode.Value);
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

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(log, force: true);
    }

    public static void ClearForBusinessEnd(string reason)
    {
        lock (SyncRoot)
        {
            var hadState = _targetRawChallengeType.Length > 0
                || _targetKind.Length > 0
                || _yuyukoRetakeEvidenceSource.Length > 0;
            ResetTargetStateLocked();
            _yuyukoRetakeEvidenceSource = "";
            CaptureFailureDiagnostics.Clear();
            CaptureFailureDiagnosticOrder.Clear();
            _lastAction = $"target cleared: {reason}";
            if (hadState) _changeVersion++;
        }

        YuumaCookerTopologyObserver.Reset(reason);
        SpecialBusinessDiagnostics.Reset();
    }

    public static SpecialBusinessContext Snapshot()
    {
        if (!RuntimeNightBusinessLifecycle.IsActive)
        {
            return new SpecialBusinessContext
            {
                Active = false,
                ChallengeTypeAvailable = true,
                ChallengeType = SpecialBusinessChallengeTypes.NotChallenge,
                Source = $"NightBusinessLifecycle={RuntimeNightBusinessLifecycle.Status}",
            };
        }

        TryAttach(_log, force: false);

        var challengeState = ReadChallengeTypeState(out var error);
        if (!challengeState.RawChallengeTypeAvailable)
        {
            ClearTargetStateForUnavailableChallenge();
        }
        else if (string.Equals(challengeState.RawChallengeType, SpecialBusinessChallengeTypes.NotChallenge, StringComparison.Ordinal))
        {
            ClearTargetStateForInactiveChallenge();
        }

        var challengeType = challengeState.EffectiveChallengeType;
        var active = challengeState.RawChallengeTypeAvailable && IsActiveChallenge(challengeType);
        var rule = GetRule(challengeType, active);
        string? displayNameError = null;
        var displayName = active ? ReadChallengeDisplayName(challengeType, out displayNameError) : "";
        error = CombineErrors(error, displayNameError);
        var target = ReadTargetForChallenge(challengeState.RawChallengeType, challengeType);
        var source = BuildSource(challengeState, target);
        if (active)
        {
            source += $"; DisplayNameSource={(displayName.Length > 0 ? ChallengeDisplayNameSource : "unavailable")}";
        }

        return new SpecialBusinessContext
        {
            Active = active,
            ChallengeTypeAvailable = challengeState.RawChallengeTypeAvailable,
            ChallengeType = challengeType,
            DisplayName = displayName,
            Category = rule.Category,
            RuleSummary = rule.RuleSummary,
            FoodTargetTags = target.FoodTargetTags.ToList(),
            YuumaFoodTargetRevision = target.YuumaFoodTargetRevision,
            BeverageTargetTags = target.BeverageTargetTags.ToList(),
            TargetFund = target.TargetFund,
            TargetLabel = target.TargetLabel,
            Phase = target.Phase,
            CurrentValue = target.CurrentValue,
            MaxValue = target.MaxValue,
            TargetValue = target.TargetValue,
            CurrentAnger = target.CurrentAnger,
            MaxAnger = target.MaxAnger,
            TargetAnger = target.TargetAnger,
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
        if (!force && !RuntimeNightBusinessLifecycle.IsActive) return;

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

            PatchExactInstanceMethod(
                _harmony,
                IncomeControllerYuumaTypeName,
                "SetTargetTag",
                new[] { typeof(string), typeof(string), typeof(bool) },
                nameof(OnYuumaTargetTagSet),
                patchedNow,
                missing);
            PatchExactInstanceMethod(
                _harmony,
                IncomeControllerYuumaTypeName,
                "SetContext",
                new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(Il2CppSystem.Action) },
                nameof(OnYuumaContextSet),
                patchedNow,
                missing);
            PatchExactInstanceMethod(
                _harmony,
                IncomeControllerYuumaTypeName,
                "SetTargetProgress",
                new[] { typeof(int) },
                nameof(OnYuumaTargetProgressSet),
                patchedNow,
                missing);
            PatchExactInstanceMethod(
                _harmony,
                IncomeControllerYuumaTypeName,
                "SetAngerProgress",
                new[] { typeof(int) },
                nameof(OnYuumaAngerProgressSet),
                patchedNow,
                missing);
            PatchExactInstanceMethod(
                _harmony,
                IncomeControllerYuumaTypeName,
                "SetTargetTime",
                new[] { typeof(float) },
                nameof(OnYuumaTargetTimeSet),
                patchedNow,
                missing);
            PatchExactInstanceMethod(
                _harmony,
                IncomeControllerYuumaTypeName,
                "SetTargetProgressImmediate",
                new[] { typeof(int), typeof(int) },
                nameof(OnYuumaTargetProgressImmediate),
                patchedNow,
                missing);
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

    private static void PatchExactInstanceMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        IReadOnlyList<Type> parameterTypes,
        string postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var signature = string.Join(",", parameterTypes.Select(type => type.FullName ?? type.Name));
        var key = $"{typeName}.{methodName}({signature})";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = RuntimeReflectionUtility.FindType(typeName);
        var target = type?.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SingleOrDefault(method =>
                method.Name == methodName
                && method.ReturnType == typeof(void)
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes));
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

    private static void OnYuumaTargetTagSet(object? __0, object? __1, object? __2)
    {
        RunCaptureCallback(
            "yuuma target tags",
            () => UpdateYuumaFoodTarget(CleanText(__0), CleanText(__1), RuntimeReflectionUtility.ToBool(__2)));
    }

    private static void OnYuumaContextSet(object? __0, object? __1, object? __2, object? __3, object? __4)
    {
        RunCaptureCallback(
            "yuuma context",
            () => UpdateProgressContext("yuuma", CleanText(__0), __1, __2, phase: "", __3, __4));
    }

    private static void OnYuumaTargetProgressSet(object? __0)
    {
        RunCaptureCallback("yuuma target progress", () => UpdateTargetValue("yuuma", __0));
    }

    private static void OnYuumaAngerProgressSet(object? __0)
    {
        RunCaptureCallback("yuuma anger progress", () => UpdateYuumaTargetAnger(__0));
    }

    private static void OnYuumaTargetTimeSet(object? __0)
    {
        RunCaptureCallback("yuuma target time", () => UpdateTargetTime("yuuma", __0));
    }

    private static void OnYuumaTargetProgressImmediate(object? __0, object? __1)
    {
        RunCaptureCallback("yuuma target progress immediate", () => UpdateYuumaImmediateProgress(__0, __1));
    }

    private static void OnKoishiTargetTagSet(object? __0)
    {
        RunCaptureCallback("koishi target tags", () => UpdateFoodTarget("koishi", CleanText(__0)));
    }

    private static void OnKoishiContextSet(object? __0, object? __1, object? __2, object? __3)
    {
        RunCaptureCallback("koishi context", () => UpdateProgressContext("koishi", CleanText(__0), __1, __2, CleanText(__3)));
    }

    private static void OnKoishiTargetProgressSet(object? __0)
    {
        RunCaptureCallback("koishi target progress", () => UpdateTargetValue("koishi", __0));
    }

    private static void OnKoishiTargetTimeSet(object? __0)
    {
        RunCaptureCallback("koishi target time", () => UpdateTargetTime("koishi", __0));
    }

    private static void OnKoishiTargetTagTimeSet(object? __0)
    {
        RunCaptureCallback("koishi target tag time", () => UpdateTargetTagTime("koishi", __0));
    }

    private static void OnKoishiShieldModeChanged(object? __0, object? __1)
    {
        RunCaptureCallback("koishi shield", () => CaptureKoishiShieldModeChanged(__0, __1));
    }

    private static void CaptureKoishiShieldModeChanged(object? brokenValue, object? recoverValue)
    {
        var broken = RuntimeReflectionUtility.ToBool(brokenValue);
        var recover = RuntimeReflectionUtility.ToBool(recoverValue);
        if (!TryReadTargetOwner("koishi", out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, "koishi");
            _koishiShieldBroken = recover ? false : broken;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = recover ? "koishi shield recovered" : $"koishi shield broken={broken}";
            _changeVersion++;
            LogWackyTargetStateLocked("koishi-shield");
        }
    }

    private static void OnKoishiClueTagsGenerated(object? __instance, object[] __args)
    {
        RunCaptureCallback("koishi clue tags", () => CaptureKoishiClueTagsGenerated(__instance, __args));
    }

    private static void CaptureKoishiClueTagsGenerated(object? instance, object[] args)
    {
        if (!string.Equals(CurrentChallengeType, SpecialBusinessChallengeTypes.WackyCookingCompetition, StringComparison.Ordinal)) return;

        var context = ResolveKoishiGenerationContext(instance, args);
        if (context == null)
        {
            LogKoishiGeneratedClues(
                "failed",
                "DisplayClass13_0 context not found",
                instance,
                args,
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
                args,
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
            args,
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
        RunCaptureCallback("yuyuko context", () => UpdateProgressContext("yuyuko", CleanText(__0), __1, __2, CleanText(__3)));
    }

    private static void OnYuyukoTargetProgressSet(object? __0)
    {
        RunCaptureCallback("yuyuko target progress", () => UpdateTargetValue("yuyuko", __0));
    }

    private static void OnYuyukoTargetTimeSet(object? __0)
    {
        RunCaptureCallback("yuyuko target time", () => UpdateTargetTime("yuyuko", __0));
    }

    private static void OnChallengeTargetFundSet(object? __0)
    {
        RunCaptureCallback("challenge target fund", () => UpdateTargetFund("challenge", __0, "目标营业额"));
    }

    private static void OnChallengeSpellCountUpdated(object? __0, object? __1)
    {
        RunCaptureCallback("challenge spell count", () => CaptureChallengeSpellCountUpdated(__0, __1));
    }

    private static void CaptureChallengeSpellCountUpdated(object? currentValue, object? totalValue)
    {
        var current = RuntimeReflectionUtility.ToInt(currentValue, int.MinValue);
        var total = RuntimeReflectionUtility.ToInt(totalValue, int.MinValue);
        if (current < 0 || total < 0) return;

        if (!TryReadTargetOwner("challenge", out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, "challenge");
            _currentSpellCount = current;
            _targetSpellCount = total;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"challenge spell count={current}/{total}";
            _changeVersion++;
        }
    }

    private static void OnMausoleumTargetFundSet(object? __0)
    {
        RunCaptureCallback("mausoleum target fund", () => UpdateTargetFund("mausoleum", __0, "目标营业额"));
    }

    private static void RunCaptureCallback(string source, Action callback)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            var diagnostic = $"{source}: {ex.GetType().Name}: {ex.GetBaseException().Message}";
            var firstOccurrence = false;
            lock (SyncRoot)
            {
                firstOccurrence = CaptureFailureDiagnostics.Add(diagnostic);
                if (firstOccurrence)
                {
                    CaptureFailureDiagnosticOrder.Enqueue(diagnostic);
                    while (CaptureFailureDiagnosticOrder.Count > MaxCaptureFailureDiagnostics)
                    {
                        CaptureFailureDiagnostics.Remove(CaptureFailureDiagnosticOrder.Dequeue());
                    }

                    _lastAction = $"{source} capture failed: {ex.GetBaseException().Message}";
                    _changeVersion++;
                }
            }
            if (firstOccurrence)
            {
                _log?.LogWarning($"Special-business {source} capture failed without affecting the game method: {ex.GetBaseException().Message}");
            }
        }
    }

    private static void UpdateTargetFund(string kind, object? value, string label)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var targetFund = RuntimeReflectionUtility.ToInt(value, int.MinValue);
        if (targetFund < 0) return;

        if (!TryReadTargetOwner(kind, out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, kind);
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
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var normalized = tags
            .Select(CleanText)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0) return;

        if (!TryReadTargetOwner(kind, out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, kind);
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

    private static void UpdateYuumaFoodTarget(string firstTag, string secondTag, bool useEffect)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;
        if (!TryReadTargetOwner("yuuma", out var rawChallengeType)) return;

        var normalized = new[] { CleanText(firstTag), CleanText(secondTag) }
            .Select(tag => FoodTags.NormalizeName(tag) ?? tag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();
        var complete = firstTag.Length > 0
            && secondTag.Length > 0
            && normalized.Length == 2;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, "yuuma");
            var identity = complete ? string.Join('\u001F', normalized) : "";
            if (complete
                && !string.Equals(
                    _yuumaFoodTargetIdentity,
                    identity,
                    StringComparison.Ordinal))
            {
                _yuumaFoodTargetRevision++;
                _yuumaFoodTargetIdentity = identity;
            }

            _foodTargetTags = complete ? normalized : Array.Empty<string>();
            _targetFund = null;
            _targetLabel = "目标料理 Tag";
            _targetTagTimeProgress = null;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"yuuma food tags={string.Join(",", _foodTargetTags)}; complete={complete}; effect={useEffect}; revision={_yuumaFoodTargetRevision}";
            _changeVersion++;
            LogYuumaTargetStateLocked("target-tags");
            SpecialBusinessDiagnostics.AppendYuumaSnapshot(
                "Blood Pond Hell HUD Target Tags",
                new[]
                {
                    $"generation: {RuntimeNightBusinessLifecycle.Generation}",
                    $"firstTag: {firstTag}",
                    $"secondTag: {secondTag}",
                    $"complete: {complete}",
                    $"distinctTagCount: {normalized.Length}",
                    $"targetRevision: {_yuumaFoodTargetRevision}",
                    $"useEffect: {useEffect}",
                },
                $"gen:{RuntimeNightBusinessLifecycle.Generation}|hud-tags|{firstTag}|{secondTag}|{useEffect}");
        }
    }

    private static void UpdateProgressContext(
        string kind,
        string label,
        object? currentValue,
        object? maxValue,
        string phase,
        object? currentAngerValue = null,
        object? maxAngerValue = null)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var current = RuntimeReflectionUtility.ToInt(currentValue, int.MinValue);
        var max = RuntimeReflectionUtility.ToInt(maxValue, int.MinValue);
        var currentAnger = RuntimeReflectionUtility.ToInt(currentAngerValue, int.MinValue);
        var maxAnger = RuntimeReflectionUtility.ToInt(maxAngerValue, int.MinValue);
        if (!TryReadTargetOwner(kind, out var rawChallengeType)) return;

        lock (SyncRoot)
        {
            var previousContextMatches = TargetContextMatchesLocked(rawChallengeType, kind);
            var previousPhase = _phase;
            var contextChanged = !previousContextMatches
                || !string.Equals(previousPhase, phase, StringComparison.Ordinal);
            SwitchTargetContextLocked(rawChallengeType, kind);
            _targetLabel = label;
            _currentValue = current == int.MinValue ? null : current;
            _maxValue = max == int.MinValue ? null : max;
            _currentAnger = currentAnger == int.MinValue ? null : currentAnger;
            _maxAnger = maxAnger == int.MinValue ? null : maxAnger;
            _phase = phase;
            if (string.Equals(kind, "yuuma", StringComparison.Ordinal))
            {
                // Native SetContext initializes both current and target progress.
                _targetValue = _currentValue;
                _targetAnger = _currentAnger;
            }
            else if (contextChanged)
            {
                _targetValue = null;
                _targetAnger = null;
                ResetTransientStateForContextLocked(kind, phase);
            }
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"{kind} context={label}; progress={_currentValue?.ToString() ?? ""}/{_maxValue?.ToString() ?? ""}; anger={_currentAnger?.ToString() ?? ""}/{_maxAnger?.ToString() ?? ""}; phase={phase}";
            _changeVersion++;
            LogWackyTargetStateLocked("context");
            LogYuyukoTargetStateLocked("context");
            LogYuumaTargetStateLocked("context");
        }
    }

    private static bool TryReadTargetOwner(string expectedKind, out string rawChallengeType)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive)
        {
            rawChallengeType = SpecialBusinessChallengeTypes.NotChallenge;
            return false;
        }

        rawChallengeType = ReadRawChallengeType(out var error);
        return string.IsNullOrWhiteSpace(error)
            && rawChallengeType.Length > 0
            && string.Equals(GetExpectedTargetKind(rawChallengeType), expectedKind, StringComparison.Ordinal);
    }

    private static void ClearTargetStateForInactiveChallenge()
    {
        lock (SyncRoot)
        {
            if (_targetRawChallengeType.Length == 0 && _targetKind.Length == 0) return;

            ResetTargetStateLocked();
            _lastAction = "target cleared: challenge inactive";
            _changeVersion++;
        }

        YuumaCookerTopologyObserver.Reset("challenge inactive");
    }

    private static void ClearTargetStateForUnavailableChallenge()
    {
        lock (SyncRoot)
        {
            if (_targetRawChallengeType.Length == 0 && _targetKind.Length == 0) return;

            ResetTargetStateLocked();
            _lastAction = "target cleared: challenge type unavailable";
            _changeVersion++;
        }

        YuumaCookerTopologyObserver.Reset("challenge type unavailable");
    }

    private static void SwitchTargetContextLocked(string rawChallengeType, string kind)
    {
        if (TargetContextMatchesLocked(rawChallengeType, kind))
        {
            return;
        }

        ResetTargetStateLocked();
        _targetRawChallengeType = rawChallengeType;
        _targetKind = kind;
        _targetBusinessGeneration = RuntimeNightBusinessLifecycle.Generation;
    }

    private static bool TargetContextMatchesLocked(string rawChallengeType, string kind)
    {
        return string.Equals(_targetRawChallengeType, rawChallengeType, StringComparison.Ordinal)
            && string.Equals(_targetKind, kind, StringComparison.Ordinal)
            && _targetBusinessGeneration == RuntimeNightBusinessLifecycle.Generation;
    }

    private static void ResetTargetStateLocked()
    {
        _targetRawChallengeType = "";
        _targetKind = "";
        _targetBusinessGeneration = 0;
        _yuumaFoodTargetRevision = 0;
        _yuumaFoodTargetIdentity = "";
        _foodTargetTags = Array.Empty<string>();
        _targetFund = null;
        _targetLabel = "";
        _phase = "";
        _currentValue = null;
        _maxValue = null;
        _targetValue = null;
        _currentAnger = null;
        _maxAnger = null;
        _targetAnger = null;
        _targetTimeProgress = null;
        _targetTagTimeProgress = null;
        _koishiShieldBroken = null;
        _koishiFoodPreferenceTags = Array.Empty<string>();
        _koishiFoodHateTags = Array.Empty<string>();
        _koishiBeveragePreferenceTags = Array.Empty<string>();
        _currentSpellCount = null;
        _targetSpellCount = null;
        _lastTargetUpdatedUtc = null;
    }

    private static void ResetTransientStateForContextLocked(string kind, string phase)
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

        _foodTargetTags = Array.Empty<string>();
        _targetTimeProgress = null;
        _targetTagTimeProgress = null;
        _koishiShieldBroken = null;
        _koishiFoodPreferenceTags = Array.Empty<string>();
        _koishiFoodHateTags = Array.Empty<string>();
        _koishiBeveragePreferenceTags = Array.Empty<string>();
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
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var target = RuntimeReflectionUtility.ToInt(value, int.MinValue);
        if (target == int.MinValue) return;

        if (!TryReadTargetOwner(kind, out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, kind);
            if (_targetValue == target) return;
            _targetValue = target;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"{kind} target progress={target}";
            _changeVersion++;
            LogWackyTargetStateLocked("target-progress");
            LogYuyukoTargetStateLocked("target-progress");
            LogYuumaTargetStateLocked("target-progress");
        }
    }

    private static void UpdateYuumaTargetAnger(object? value)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var target = RuntimeReflectionUtility.ToInt(value, int.MinValue);
        if (target == int.MinValue) return;

        if (!TryReadTargetOwner("yuuma", out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, "yuuma");
            if (_targetAnger == target) return;
            _targetAnger = target;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"yuuma target anger={target}";
            _changeVersion++;
            LogYuumaTargetStateLocked("target-anger");
        }
    }

    private static void UpdateYuumaImmediateProgress(object? targetValueValue, object? targetAngerValue)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var targetValue = RuntimeReflectionUtility.ToInt(targetValueValue, int.MinValue);
        var targetAnger = RuntimeReflectionUtility.ToInt(targetAngerValue, int.MinValue);
        if (targetValue == int.MinValue || targetAnger == int.MinValue) return;

        if (!TryReadTargetOwner("yuuma", out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, "yuuma");
            if (_targetValue == targetValue && _targetAnger == targetAnger) return;
            _targetValue = targetValue;
            _targetAnger = targetAnger;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"yuuma immediate targets hp={targetValue}; anger={targetAnger}";
            _changeVersion++;
            LogYuumaTargetStateLocked("target-progress-immediate");
        }
    }

    private static void UpdateTargetTime(string kind, object? value)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var progress = ToDouble(value, double.NaN);
        if (double.IsNaN(progress)) return;

        if (!TryReadTargetOwner(kind, out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, kind);
            var normalizedProgress = ClampProgress(progress);
            if (string.Equals(kind, "yuuma", StringComparison.Ordinal)
                && ProgressBucket(_targetTimeProgress) == ProgressBucket(normalizedProgress))
            {
                return;
            }

            _targetTimeProgress = normalizedProgress;
            _lastTargetUpdatedUtc = DateTime.UtcNow;
            _lastAction = $"{kind} time={FormatProgress(_targetTimeProgress)}";
            _changeVersion++;
            LogWackyTargetStateLocked("target-time", bucketByTime: true);
            LogYuyukoTargetStateLocked("target-time", bucketByTime: true);
            LogYuumaTargetStateLocked("target-time", bucketByTime: true);
        }
    }

    private static void UpdateTargetTagTime(string kind, object? value)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        var progress = ToDouble(value, double.NaN);
        if (double.IsNaN(progress)) return;

        if (!TryReadTargetOwner(kind, out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, kind);
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
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        if (!TryReadTargetOwner("koishi", out var rawChallengeType)) return;
        lock (SyncRoot)
        {
            SwitchTargetContextLocked(rawChallengeType, "koishi");
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

    private static void LogYuumaTargetStateLocked(
        string eventName,
        bool bucketByTime = false)
    {
        if (!string.Equals(_targetKind, "yuuma", StringComparison.Ordinal)) return;

        var generation = RuntimeNightBusinessLifecycle.Generation;
        var lines = new[]
        {
            $"event: {eventName}",
            $"generation: {generation}",
            $"targetOwnerGeneration: {_targetBusinessGeneration}",
            $"targetKind: {_targetKind}",
            $"foodTargetTags: {SpecialBusinessDiagnostics.FormatTags(_foodTargetTags)}",
            $"targetLabel: {_targetLabel}",
            $"currentValue: {_currentValue?.ToString() ?? ""}",
            $"maxValue: {_maxValue?.ToString() ?? ""}",
            $"targetValue: {_targetValue?.ToString() ?? ""}",
            $"currentAnger: {_currentAnger?.ToString() ?? ""}",
            $"maxAnger: {_maxAnger?.ToString() ?? ""}",
            $"targetAnger: {_targetAnger?.ToString() ?? ""}",
            $"targetTimeProgress: {FormatProgress(_targetTimeProgress)}",
            $"lastAction: {_lastAction}",
            $"changeVersion: {_changeVersion}",
        };

        if (bucketByTime)
        {
            SpecialBusinessDiagnostics.AppendYuumaProgressSnapshot(
                $"gen:{generation}|hud-time|{string.Join(",", _foodTargetTags)}",
                _targetTimeProgress,
                "Blood Pond Hell HUD Target Time",
                lines);
            return;
        }

        SpecialBusinessDiagnostics.AppendYuumaSnapshot(
            "Blood Pond Hell HUD Target Updated",
            lines,
            $"gen:{generation}|hud|{eventName}|{string.Join(",", _foodTargetTags)}|{_currentValue}|{_maxValue}|{_targetValue}|{_currentAnger}|{_maxAnger}|{_targetAnger}");
    }

    private static ChallengeTypeState ReadChallengeTypeState(out string? error)
    {
        var rawChallengeType = ReadRawChallengeType(out var rawError);
        var difficultyMode = ReadDifficultyMode(out var difficultyError);
        var effectiveChallengeType = rawChallengeType;
        var variantSource = "";

        lock (SyncRoot)
        {
            if (string.IsNullOrWhiteSpace(rawError))
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
        }

        error = CombineErrors(rawError, difficultyError);
        return new ChallengeTypeState(
            rawChallengeType,
            effectiveChallengeType,
            difficultyMode,
            variantSource,
            string.IsNullOrWhiteSpace(rawError));
    }

    private static string ReadChallengeDisplayName(string challengeType, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(challengeType))
        {
            error = "challenge type is empty while reading display name";
            return "";
        }

        var now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            if (ChallengeDisplayNameCache.TryGetValue(challengeType, out var cached)
                && !cached.ShouldRetry(now))
            {
                error = cached.Error;
                return cached.DisplayName;
            }
        }

        var resolution = ResolveChallengeDisplayName(challengeType, now);
        lock (SyncRoot)
        {
            ChallengeDisplayNameCache[challengeType] = resolution;
        }

        error = resolution.Error;
        return resolution.DisplayName;
    }

    private static ChallengeDisplayNameResolution ResolveChallengeDisplayName(string challengeType, DateTime now)
    {
        var stage = "type-discovery";
        try
        {
            var directorType = RuntimeReflectionUtility.FindType(NightSceneDirectorTypeName);
            if (directorType == null)
            {
                return ChallengeDisplayNameResolution.RetryableFailure(
                    "NightSceneDirector proxy type not found while reading challenge display name",
                    now + RetryInterval);
            }

            var challengeEnumType = directorType.GetNestedType(
                ChallengeTypeNestedName,
                BindingFlags.Public | BindingFlags.NonPublic);
            if (challengeEnumType == null)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    "NightSceneDirector.ChallengeType proxy enum not found");
            }

            var runtimeChallengeEnumType = Il2CppType.From(challengeEnumType, false);
            if (runtimeChallengeEnumType is null)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    "NightSceneDirector.ChallengeType IL2CPP type not found");
            }

            var field = runtimeChallengeEnumType.GetField(challengeType);
            if (field is null)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP field not found: {challengeType}");
            }

            var inspectorNameType = Il2CppType.Of<InspectorNameAttribute>(false);
            if (inspectorNameType is null)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    "UnityEngine.InspectorNameAttribute IL2CPP type not found");
            }

            stage = "custom-attributes";
            Il2CppSystem.Reflection.CustomAttributeData? inspectorName = null;
            var attributes = Il2CppSystem.Reflection.CustomAttributeData.GetCustomAttributes(field);
            if (attributes is null)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP custom attribute list not found: {challengeType}");
            }

            var attributeCount = attributes
                .Cast<Il2CppSystem.Collections.Generic.ICollection<Il2CppSystem.Reflection.CustomAttributeData>>()
                .Count;
            for (var index = 0; index < attributeCount; index++)
            {
                var attribute = attributes[index];
                if (attribute is null)
                {
                    return ChallengeDisplayNameResolution.PermanentFailure(
                        $"ChallengeType IL2CPP custom attribute entry is null: {challengeType}[{index}]");
                }

                var attributeType = attribute.AttributeType;
                if (attributeType is null)
                {
                    return ChallengeDisplayNameResolution.PermanentFailure(
                        $"ChallengeType IL2CPP custom attribute type is null: {challengeType}[{index}]");
                }

                if (!attributeType.Equals(inspectorNameType)) continue;
                inspectorName = attribute;
                break;
            }

            if (inspectorName is null)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP InspectorName metadata not found: {challengeType}");
            }

            stage = "constructor-arguments";
            var constructorArguments = inspectorName.ConstructorArguments;
            if (constructorArguments is null)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP InspectorName constructor arguments not found: {challengeType}");
            }

            var constructorArgumentCount = constructorArguments
                .Cast<Il2CppSystem.Collections.Generic.ICollection<Il2CppSystem.Reflection.CustomAttributeTypedArgument>>()
                .Count;
            if (constructorArgumentCount == 0)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP InspectorName constructor argument is missing: {challengeType}");
            }

            var constructorArgument = constructorArguments[0];
            if (constructorArgument is null)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP InspectorName constructor argument is null: {challengeType}");
            }

            stage = "argument-value";
            var argumentValue = constructorArgument.Value;
            if (argumentValue is null)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP InspectorName value is null: {challengeType}");
            }

            var valuePointer = argumentValue.Pointer;
            if (valuePointer == IntPtr.Zero)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP InspectorName string pointer is null: {challengeType}");
            }

            stage = "argument-class";
            var stringClass = Il2CppClassPointerStore<string>.NativeClassPtr;
            var valueClass = IL2CPP.il2cpp_object_get_class(valuePointer);
            if (stringClass == IntPtr.Zero || valueClass != stringClass)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP InspectorName value object is not System.String: {challengeType}");
            }

            stage = "string-decode";
            var displayName = IL2CPP.Il2CppStringToManaged(valuePointer)?.Trim() ?? "";
            if (displayName.Length == 0)
            {
                return ChallengeDisplayNameResolution.PermanentFailure(
                    $"ChallengeType IL2CPP InspectorName metadata is empty: {challengeType}");
            }

            return ChallengeDisplayNameResolution.Success(displayName);
        }
        catch (Exception ex)
        {
            return ChallengeDisplayNameResolution.RetryableFailure(
                $"challenge display name IL2CPP metadata read failed at stage={stage}: {ex.GetType().Name}: {ex.Message}",
                now + ChallengeDisplayNameExceptionRetryInterval);
        }
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
            if (value is null)
            {
                error = "NightSceneDirector.ChallengeMode value not found";
                return SpecialBusinessChallengeTypes.NotChallenge;
            }

            var text = CleanText(value);
            if (text.Length == 0)
            {
                error = "NightSceneDirector.ChallengeMode value is empty";
                return SpecialBusinessChallengeTypes.NotChallenge;
            }

            return NormalizeChallengeTypeText(text);
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
            "6" => SpecialBusinessChallengeTypes.BloodPondHell,
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

    private static SpecialBusinessTarget ReadTargetForChallenge(string rawChallengeType, string challengeType)
    {
        var expectedKind = GetExpectedTargetKind(challengeType);

        if (expectedKind.Length == 0) return SpecialBusinessTarget.Empty;

        lock (SyncRoot)
        {
            if (!TargetContextMatchesLocked(rawChallengeType, expectedKind))
            {
                return SpecialBusinessTarget.Empty;
            }

            return new SpecialBusinessTarget(
                NormalizeFoodTargetTagsLocked(),
                string.Equals(expectedKind, "yuuma", StringComparison.Ordinal)
                    ? _yuumaFoodTargetRevision
                    : 0,
                Array.Empty<string>(),
                _targetFund,
                _targetLabel,
                _phase,
                _currentValue,
                _maxValue,
                _targetValue,
                _currentAnger,
                _maxAnger,
                _targetAnger,
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

    private static string GetExpectedTargetKind(string challengeType)
    {
        return challengeType switch
        {
            SpecialBusinessChallengeTypes.BloodPondHell => "yuuma",
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
        var source = $"ChallengeTypeAvailable={challengeState.RawChallengeTypeAvailable}; ChallengeMode={challengeState.EffectiveChallengeType}; RawChallengeMode={challengeState.RawChallengeType}; DifficultyMode={challengeState.DifficultyMode}; VariantSource={challengeState.VariantSource}; Capture={BuildCaptureStatus(target)}";
        if (target.Source.Length > 0)
        {
            source += $"; TargetSource={target.Source}";
        }

        return source;
    }

    private static string BuildCaptureStatus(SpecialBusinessTarget target)
    {
        lock (SyncRoot)
        {
            var targetDescription = target.Source.Length == 0
                ? "none"
                : $"{target.Source}; generation={_targetBusinessGeneration}; foodTags={string.Join(",", target.FoodTargetTags)}; targetFund={target.TargetFund?.ToString() ?? ""}; progress={target.CurrentValue?.ToString() ?? ""}/{target.MaxValue?.ToString() ?? ""}; target={target.TargetValue?.ToString() ?? ""}; anger={target.CurrentAnger?.ToString() ?? ""}/{target.MaxAnger?.ToString() ?? ""}; targetAnger={target.TargetAnger?.ToString() ?? ""}; time={FormatProgress(target.TargetTimeProgress)}; tagTime={FormatProgress(target.TargetTagTimeProgress)}; koishiShield={target.WackyKoishiShieldBroken?.ToString() ?? ""}; koishiFoodLikes={string.Join(",", target.WackyKoishiFoodPreferenceTags)}; koishiFoodHates={string.Join(",", target.WackyKoishiFoodHateTags)}; koishiBevLikes={string.Join(",", target.WackyKoishiBeveragePreferenceTags)}; spells={target.CurrentSpellCount?.ToString() ?? ""}/{target.TargetSpellCount?.ToString() ?? ""}; phase={target.Phase}";
            var yuyukoVariant = _yuyukoRetakeEvidenceSource.Length == 0
                ? ""
                : $"; yuyukoRetakeEvidence={_yuyukoRetakeEvidenceSource}";
            return $"{_status}; version={_changeVersion}; owner={_targetRawChallengeType}; target={targetDescription}; last={_lastAction}{yuyukoVariant}";
        }
    }

    private static string? CombineErrors(params string?[] errors)
    {
        var combined = string.Join(
            "; ",
            errors
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.Ordinal));
        return combined.Length == 0 ? null : combined;
    }

    private readonly record struct ChallengeTypeState(
        string RawChallengeType,
        string EffectiveChallengeType,
        string DifficultyMode,
        string VariantSource,
        bool RawChallengeTypeAvailable);

    private readonly record struct ChallengeDisplayNameResolution(
        string DisplayName,
        string? Error,
        DateTime NextRetryUtc)
    {
        public bool ShouldRetry(DateTime now)
        {
            return DisplayName.Length == 0 && now >= NextRetryUtc;
        }

        public static ChallengeDisplayNameResolution Success(string displayName)
        {
            return new ChallengeDisplayNameResolution(displayName, null, DateTime.MaxValue);
        }

        public static ChallengeDisplayNameResolution PermanentFailure(string error)
        {
            return new ChallengeDisplayNameResolution("", error, DateTime.MaxValue);
        }

        public static ChallengeDisplayNameResolution RetryableFailure(string error, DateTime nextRetryUtc)
        {
            return new ChallengeDisplayNameResolution("", error, nextRetryUtc);
        }
    }

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

    private static int ProgressBucket(double? value, int bucketCount = 20)
    {
        if (!value.HasValue || double.IsNaN(value.Value)) return -1;
        return Math.Clamp((int)Math.Floor(value.Value * bucketCount), 0, bucketCount);
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
        long YuumaFoodTargetRevision,
        IReadOnlyList<string> BeverageTargetTags,
        int? TargetFund,
        string TargetLabel,
        string Phase,
        int? CurrentValue,
        int? MaxValue,
        int? TargetValue,
        int? CurrentAnger,
        int? MaxAnger,
        int? TargetAnger,
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
            0,
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
