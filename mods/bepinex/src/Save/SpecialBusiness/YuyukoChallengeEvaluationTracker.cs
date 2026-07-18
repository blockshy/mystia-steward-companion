using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

internal static class YuyukoChallengeEvaluationTracker
{
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string SpecialGuestsControllerTypeName = "NightScene.GuestManagementUtility.SpecialGuestsController";
    private static readonly object SyncRoot = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> RecentTraceEvents = new(StringComparer.Ordinal);
    private static readonly TimeSpan TraceThrottle = TimeSpan.FromSeconds(2);
    private static Harmony? _harmony;
    private static string _status = "not attached";
    private static int _traceEvents;
    private static string _lastTrace = "";

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return $"{_status}; events={_traceEvents}; last={RuntimeReflectionUtility.Trim(_lastTrace, 160)}";
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        var patchedNow = new List<string>();
        var missing = new List<string>();
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.yuyuko-evaluation-tracker");
            PatchMethod(_harmony, GuestsManagerTypeName, "EvaulateManualOrder", 2, false, nameof(OnManualOrderEvaluating), patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "EvaluateOrder", 3, false, nameof(OnRuntimeOrderEvaluating), patchedNow, missing);
            PatchMethod(_harmony, SpecialGuestsControllerTypeName, "PostEvaluation", 4, false, nameof(OnSpecialPostEvaluation), patchedNow, missing);

            lock (SyncRoot)
            {
                _status = PatchedMethods.Count == 0
                    ? $"waiting: {string.Join(", ", missing.Take(4))}"
                    : $"patched={PatchedMethods.Count}";
            }

            if (patchedNow.Count > 0)
            {
                log.LogInfo($"Yuyuko challenge evaluation tracker patched: {string.Join(", ", patchedNow)}.");
            }
            else if (PatchedMethods.Count == 0)
            {
                log.LogWarning($"Yuyuko challenge evaluation tracker waiting for game types: {string.Join(", ", missing.Take(4))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _status = $"error: {ex.Message}";
            }

            log.LogWarning($"Yuyuko challenge evaluation tracker failed: {ex.Message}");
        }
    }

    public static bool TryFindYuyukoStoryPhase3ScoreCallback(object? callback, out string detail)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive)
        {
            detail = "night business unavailable";
            return false;
        }

        detail = "callback missing";
        if (callback == null) return false;

        var entries = EnumerateCallbackEntries(callback).ToArray();
        if (entries.Length == 0)
        {
            entries = new[] { callback };
        }

        foreach (var entry in entries)
        {
            if (IsYuyukoStoryScoreCallbackEntry(entry))
            {
                detail = $"matched story score callback: {DescribeCallbackEntry(entry)}";
                return true;
            }
        }

        detail = string.Join(" | ", entries.Select(DescribeCallbackEntry).Take(6));
        return false;
    }

    public static bool TryFindYuyukoRetakePhase3ProgressCallback(object? callback, out string detail)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive)
        {
            detail = "night business unavailable";
            return false;
        }

        detail = "callback missing";
        if (callback == null) return false;

        var entries = EnumerateCallbackEntries(callback).ToArray();
        if (entries.Length == 0)
        {
            entries = new[] { callback };
        }

        foreach (var entry in entries)
        {
            if (IsYuyukoRetakeProgressCallbackEntry(entry))
            {
                detail = $"matched retake progress callback: {DescribeCallbackEntry(entry)}";
                RuntimeSpecialBusinessContextService.MarkYuyukoRetakeEvidence(RuntimeReflectionUtility.Trim(detail, 220));
                return true;
            }
        }

        detail = string.Join(" | ", entries.Select(DescribeCallbackEntry).Take(6));
        return false;
    }

    public static bool TryFindYuyukoPhase3ManualProgressCallback(object? callback, out string detail)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive)
        {
            detail = "night business unavailable";
            return false;
        }

        detail = "callback missing";
        if (callback == null) return false;

        var entries = EnumerateCallbackEntries(callback).ToArray();
        if (entries.Length == 0)
        {
            entries = new[] { callback };
        }

        foreach (var entry in entries)
        {
            if (IsYuyukoPhase3ManualProgressCallbackEntry(entry))
            {
                detail = $"matched manual progress callback: {DescribeCallbackEntry(entry)}";
                return true;
            }
        }

        detail = string.Join(" | ", entries.Select(DescribeCallbackEntry).Take(6));
        return false;
    }

    public static string DescribeCallback(object? callback)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return "night business unavailable";
        if (callback == null) return "null";
        var entries = EnumerateCallbackEntries(callback).ToArray();
        if (entries.Length <= 1) return DescribeCallbackEntry(callback);
        return string.Join(" | ", entries.Select(DescribeCallbackEntry).Take(6));
    }

    private static void PatchMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        bool isStatic,
        string prefixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}/{(isStatic ? "static" : "instance")}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = RuntimeReflectionUtility.FindType(typeName);
        if (type == null)
        {
            missing.Add(typeName);
            return;
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var target = type
            .GetMethods(flags)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
        var prefix = typeof(YuyukoChallengeEvaluationTracker).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || prefix == null)
        {
            missing.Add(key);
            return;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        lock (SyncRoot)
        {
            PatchedMethods.Add(key);
        }

        patchedNow.Add(key);
    }

    private static void OnManualOrderEvaluating(object? __0, object? __1)
    {
        RunEvaluationCallback(() => AppendNativeEvaluationTrace(
                "GuestsManager.EvaulateManualOrder",
                __0,
                __1,
                "manualEvaluation: true"));
    }

    private static void OnRuntimeOrderEvaluating(object? __0, object? __1, object? __2)
    {
        RunEvaluationCallback(() => AppendNativeEvaluationTrace(
            "GuestsManager.EvaluateOrder",
            __0,
            __2,
            "manualEvaluation: false",
            $"finishedByPartner: {FormatValue(__1)}"));
    }

    private static void OnSpecialPostEvaluation(object? __instance, object? __0, object? __1, object? __2, object? __3)
    {
        RunEvaluationCallback(() => AppendNativeEvaluationTrace(
            "SpecialGuestsController.PostEvaluation",
            __instance,
            __1,
            $"evaluationType: {FormatValue(__0)}",
            $"finishedByPartner: {FormatValue(__2)}",
            $"obtainedExGoodRatingWithModifiers: {FormatValue(__3)}"));
    }

    private static void RunEvaluationCallback(Action callback)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        try
        {
            callback();
        }
        catch
        {
            // Harmony diagnostics must never affect the game's evaluation path.
        }
    }

    private static void AppendNativeEvaluationTrace(
        string eventName,
        object? controller,
        object? callback,
        params string[] extraLines)
    {
        if (!RuntimeSpecialBusinessContextService.IsActiveYuyukoPhase("Phase3")) return;
        if (ShouldThrottle(eventName, controller, callback, extraLines)) return;

        try
        {
            var order = RuntimeReflectionUtility.InvokeMethod(controller, "PeekOrders");
            var evaluationCallback = ReadControllerCallback(controller, "OverrideEvaluationCallback");
            var onEvalFinishCallback = ReadControllerCallback(controller, "OnEvalFinishCallback");
            var onExtraFinishEvaluationCallback = ReadControllerCallback(controller, "OnExtraFinishEvaluationCallback");
            var onFinishOrderCallback = ReadControllerCallback(controller, "OnFinishOrderCallback");
            var hasScoreCallback = TryFindYuyukoStoryPhase3ScoreCallback(evaluationCallback, out var scoreCallbackDetail);
            var hasRetakeProgressCallback = TryFindYuyukoRetakePhase3ProgressCallback(evaluationCallback, out var retakeProgressCallbackDetail);
            var hasManualProgressCallback = TryFindYuyukoPhase3ManualProgressCallback(callback, out var manualProgressCallbackDetail);

            var lines = new List<string>
            {
                $"event: {eventName}",
                $"challengeType: {RuntimeSpecialBusinessContextService.CurrentChallengeType}",
                $"phase3Active: {RuntimeSpecialBusinessContextService.IsActiveYuyukoPhase("Phase3")}",
                $"controller: {SpecialBusinessDiagnostics.DescribeObject(controller)}",
                $"controllerType: {controller?.GetType().FullName ?? ""}",
                $"controllerGuest: {DescribeRuntimeGuest(ReadControllerGuest(controller))}",
                $"controllerDeskCode: {ReadRuntimeText(controller, "DeskCode")}",
                $"isControlled: {ReadRuntimeText(controller, "IsControlled")}",
                $"isHerself: {ReadRuntimeText(controller, "IsHerself")}",
                $"remainOrderCount: {ReadRuntimeText(controller, "RemainOrderCount")}",
                $"hasEvaluated: {ReadRuntimeText(controller, "HasEvaluated")}",
                $"order: {SpecialBusinessDiagnostics.DescribeObject(order)}",
                $"orderFullfilled: {ReadRuntimeText(order, "IsFullfilled")}",
                $"orderGuest: {DescribeRuntimeGuest(ReadOrderGuest(order))}",
                $"servFood: {SpecialBusinessDiagnostics.DescribeObject(ReadMember(order, "ServFood"))}",
                $"servBeverage: {SpecialBusinessDiagnostics.DescribeObject(ReadMember(order, "ServBeverage"))}",
                $"callbackArgument: {SpecialBusinessDiagnostics.DescribeObject(callback)}",
                $"callbackArgumentDetail: {DescribeCallback(callback)}",
                $"hasYuyukoManualProgressCallback: {hasManualProgressCallback}",
                $"manualProgressCallbackDetail: {manualProgressCallbackDetail}",
                $"evaluationCallback: {SpecialBusinessDiagnostics.DescribeObject(evaluationCallback)}",
                $"evaluationCallbackDetail: {DescribeCallback(evaluationCallback)}",
                $"hasYuyukoScoreCallback: {hasScoreCallback}",
                $"scoreCallbackDetail: {scoreCallbackDetail}",
                $"hasYuyukoRetakeProgressCallback: {hasRetakeProgressCallback}",
                $"retakeProgressCallbackDetail: {retakeProgressCallbackDetail}",
                $"onEvalFinishCallback: {SpecialBusinessDiagnostics.DescribeObject(onEvalFinishCallback)}",
                $"onEvalFinishCallbackDetail: {DescribeCallback(onEvalFinishCallback)}",
                $"onExtraFinishEvaluationCallback: {SpecialBusinessDiagnostics.DescribeObject(onExtraFinishEvaluationCallback)}",
                $"onExtraFinishEvaluationCallbackDetail: {DescribeCallback(onExtraFinishEvaluationCallback)}",
                $"onFinishOrderCallback: {SpecialBusinessDiagnostics.DescribeObject(onFinishOrderCallback)}",
                $"onFinishOrderCallbackDetail: {DescribeCallback(onFinishOrderCallback)}",
                $"yuyukoProgress: {RuntimeSpecialBusinessContextService.DescribeYuyukoProgressForDiagnostics()}",
                $"specialBusinessStatus: {RuntimeSpecialBusinessContextService.Status}",
            };
            lines.AddRange(extraLines.Where(line => !string.IsNullOrWhiteSpace(line)));

            SpecialBusinessDiagnostics.AppendYuyukoSnapshot("Yuyuko Challenge Native Evaluation Trace", lines);
            lock (SyncRoot)
            {
                _traceEvents++;
                _lastTrace = $"{eventName}; controller={SpecialBusinessDiagnostics.DescribeObject(controller)}; callback={SpecialBusinessDiagnostics.DescribeObject(callback)}; manualProgress={hasManualProgressCallback}; score={hasScoreCallback}; retakeProgress={hasRetakeProgressCallback}";
                _status = PatchedMethods.Count == 0 ? _status : $"patched={PatchedMethods.Count}";
            }
        }
        catch
        {
            // Evaluation tracing must never affect gameplay.
        }
    }

    private static bool ShouldThrottle(
        string eventName,
        object? controller,
        object? callback,
        IEnumerable<string> extraLines)
    {
        var now = DateTime.UtcNow;
        var key = string.Join(
            "|",
            eventName,
            ObjectKey(controller),
            ObjectKey(callback),
            string.Join(",", extraLines),
            RuntimeSpecialBusinessContextService.DescribeYuyukoProgressForDiagnostics());

        lock (SyncRoot)
        {
            if (RecentTraceEvents.TryGetValue(key, out var last)
                && now - last < TraceThrottle)
            {
                return true;
            }

            RecentTraceEvents[key] = now;
            if (RecentTraceEvents.Count > 256)
            {
                foreach (var staleKey in RecentTraceEvents
                    .Where(pair => now - pair.Value > TimeSpan.FromMinutes(2))
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    RecentTraceEvents.Remove(staleKey);
                }
            }
        }

        return false;
    }

    private static bool IsYuyukoPhase3ManualProgressCallbackEntry(object? entry)
    {
        if (entry == null) return false;
        var text = DescribeCallbackEntry(entry);
        return text.Contains("YuyukoBossData", StringComparison.Ordinal)
            && (text.Contains("MainChallengeLoop", StringComparison.Ordinal)
                || text.Contains("DisplayClass16_5", StringComparison.Ordinal)
                || text.Contains("b__37", StringComparison.Ordinal));
    }

    private static bool IsYuyukoStoryScoreCallbackEntry(object? entry)
    {
        if (entry == null) return false;
        var text = DescribeCallbackEntry(entry);
        return text.Contains("YuyukoOverrideEvaluationCallback_33", StringComparison.Ordinal)
            || (text.Contains("YuyukoBossData", StringComparison.Ordinal)
                && text.Contains("DisplayClass16_0", StringComparison.Ordinal));
    }

    private static bool IsYuyukoRetakeProgressCallbackEntry(object? entry)
    {
        if (entry == null) return false;
        var text = DescribeCallbackEntry(entry);
        return text.Contains("YuyukoOverrideEvaluationCallback_50", StringComparison.Ordinal)
            || text.Contains("GroupOverrideEvaluationCallback_70", StringComparison.Ordinal)
            || (text.Contains("YuyukoBossData", StringComparison.Ordinal)
                && text.Contains("DisplayClass16_6", StringComparison.Ordinal)
                && text.Contains("YuyukoOverrideEvaluationCallback", StringComparison.Ordinal))
            || (text.Contains("YuyukoBossData", StringComparison.Ordinal)
                && text.Contains("DisplayClass16_9", StringComparison.Ordinal)
                && text.Contains("GroupOverrideEvaluationCallback", StringComparison.Ordinal));
    }

    private static IEnumerable<object> EnumerateCallbackEntries(object? callback)
    {
        if (callback == null) yield break;
        if (callback is Delegate managedDelegate)
        {
            foreach (var entry in managedDelegate.GetInvocationList())
            {
                yield return entry;
            }

            yield break;
        }

        var invocationList = RuntimeReflectionUtility.InvokeMethod(callback, "GetInvocationList")
            ?? RuntimeReflectionUtility.InvokeMethod(callback, "get_InvocationList")
            ?? ReadMember(callback, "delegates")
            ?? ReadMember(callback, "invocationList")
            ?? ReadMember(callback, "m_invocationList");
        var emitted = false;
        foreach (var entry in RuntimeReflectionUtility.EnumerateObjects(invocationList))
        {
            if (entry == null) continue;
            emitted = true;
            yield return entry;
        }

        if (!emitted)
        {
            yield return callback;
        }
    }

    private static string DescribeCallbackEntry(object? callback)
    {
        if (callback == null) return "null";
        var parts = new List<string>
        {
            SpecialBusinessDiagnostics.DescribeObject(callback),
        };

        if (callback.GetType().FullName is { Length: > 0 } typeName)
        {
            parts.Add($"runtimeType={typeName}");
        }

        if (callback is Delegate managedDelegate)
        {
            parts.Add($"method={FormatMethodInfo(managedDelegate.Method)}");
            parts.Add($"target={SpecialBusinessDiagnostics.DescribeObject(managedDelegate.Target)}");
        }
        else
        {
            AppendCallbackMember(parts, "method", callback, "Method", "method", "method_info", "method_name");
            AppendCallbackMember(parts, "target", callback, "Target", "target", "m_target", "_target");
        }

        AppendCallbackMember(parts, "methodPtr", callback, "method_ptr", "methodPtr", "m_methodPtr");
        AppendCallbackMember(parts, "invokeImpl", callback, "invoke_impl", "invokeImpl", "m_invokeImpl");
        AppendCallbackMember(parts, "methodCode", callback, "method_code", "methodCode");
        AppendCallbackMember(parts, "delegateTrampoline", callback, "delegate_trampoline", "delegateTrampoline");
        return string.Join(" ", parts);
    }

    private static void AppendCallbackMember(ICollection<string> parts, string label, object callback, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadMember(callback, name) ?? RuntimeReflectionUtility.InvokeMethod(callback, $"get_{name}");
            if (value == null) continue;

            parts.Add($"{label}={(string.Equals(label, "method", StringComparison.Ordinal) ? FormatCallbackMethodValue(value) : FormatCallbackValue(value))}");
            return;
        }
    }

    private static string FormatCallbackMethodValue(object value)
    {
        if (value is MethodInfo methodInfo) return FormatMethodInfo(methodInfo);

        var name = ReadRuntimeText(value, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadRuntimeText(value, "name");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var declaringType = ReadMember(value, "DeclaringType")
                ?? RuntimeReflectionUtility.InvokeMethod(value, "get_DeclaringType")
                ?? ReadMember(value, "declaring_type");
            var declaringTypeName = ReadRuntimeText(declaringType, "FullName");
            if (string.IsNullOrWhiteSpace(declaringTypeName))
            {
                declaringTypeName = ReadRuntimeText(declaringType, "Name");
            }

            return string.IsNullOrWhiteSpace(declaringTypeName) ? name : $"{declaringTypeName}.{name}";
        }

        return FormatCallbackValue(value);
    }

    private static string FormatCallbackValue(object value)
    {
        if (value is MethodInfo methodInfo) return FormatMethodInfo(methodInfo);
        if (value is Delegate callback) return DescribeCallbackEntry(callback);
        if (value is IntPtr intPtr) return $"0x{intPtr.ToInt64():X}";
        if (value is UIntPtr uintPtr) return $"0x{uintPtr.ToUInt64():X}";
        if (value is string text) return text.Trim();
        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Enum)
        {
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        return SpecialBusinessDiagnostics.DescribeObject(value);
    }

    private static string FormatMethodInfo(MethodInfo methodInfo)
    {
        try
        {
            var declaringType = methodInfo.DeclaringType?.FullName ?? "";
            var parameters = string.Join(",", methodInfo.GetParameters().Select(parameter => parameter.ParameterType.Name));
            return $"{declaringType}.{methodInfo.Name}({parameters})";
        }
        catch
        {
            return methodInfo.ToString() ?? "";
        }
    }

    private static object? ReadControllerCallback(object? controller, string name)
    {
        return ReadMember(controller, name)
            ?? RuntimeReflectionUtility.InvokeMethod(controller, $"get_{name}")
            ?? ReadMember(controller, $"<{name}>k__BackingField")
            ?? ReadMember(controller, $"_{name}_k__BackingField");
    }

    private static object? ReadControllerGuest(object? controller)
    {
        return ReadMember(controller, "SpecialGuest")
            ?? RuntimeReflectionUtility.InvokeMethod(controller, "get_SpecialGuest")
            ?? ReadMember(controller, "OrderingGuest")
            ?? RuntimeReflectionUtility.InvokeMethod(controller, "get_OrderingGuest");
    }

    private static object? ReadOrderGuest(object? order)
    {
        return ReadMember(order, "SpecialGuests")
            ?? RuntimeReflectionUtility.InvokeMethod(order, "get_SpecialGuests")
            ?? ReadMember(order, "Guest")
            ?? RuntimeReflectionUtility.InvokeMethod(order, "get_Guest");
    }

    private static string DescribeRuntimeGuest(object? guest)
    {
        if (guest == null) return "null";
        var id = RuntimeReflectionUtility.ToInt(
            RuntimeReflectionUtility.InvokeMethod(guest, "get_id")
            ?? RuntimeReflectionUtility.InvokeMethod(guest, "get_Id")
            ?? RuntimeReflectionUtility.InvokeMethod(guest, "get_CharacterID")
            ?? ReadMember(guest, "id")
            ?? ReadMember(guest, "Id")
            ?? ReadMember(guest, "CharacterID"),
            -1);
        var name = ReadRuntimeText(guest, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadRuntimeText(guest, "ShowName");
        }

        return $"{SpecialBusinessDiagnostics.DescribeObject(guest)} id={(id >= 0 ? id.ToString() : "")} name={name}";
    }

    private static string ReadRuntimeText(object? instance, string name)
    {
        if (instance == null) return "";
        var value = ReadMember(instance, name)
            ?? RuntimeReflectionUtility.InvokeMethod(instance, $"get_{name}")
            ?? RuntimeReflectionUtility.InvokeMethod(instance, name);
        return value?.ToString()?.Trim() ?? "";
    }

    private static object? ReadMember(object? instance, string name)
    {
        return RuntimeReflectionUtility.GetMemberValue(instance, name);
    }

    private static string FormatValue(object? value)
    {
        return value?.ToString()?.Trim() ?? "";
    }

    private static string ObjectKey(object? value)
    {
        return value == null ? "null" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value).ToString("X");
    }
}
