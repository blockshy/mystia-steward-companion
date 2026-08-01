using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 捕获游戏运行时产生的稀客订单，并为本地 API 和自动上菜流程提供可复用的订单快照。
/// </summary>
/// <remarks>
/// 游戏没有稳定的“当前稀客订单列表”公开接口，因此这里通过 Harmony 监听订单与控制器的绑定、清理和状态更新等关键点。
/// 捕获结果只保存在内存中；只有原生绑定成功的订单会进入缓存，后续回调按运行时对象指针更新或移除该绑定。
/// </remarks>
public static class SpecialOrderRuntimeCapture
{
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
    private const string EvaluationResultTypeName =
        "NightScene.GuestManagementUtility.GuestGroupController+EvaluationResult";
    private const string LeaveTypeName =
        "NightScene.GuestManagementUtility.GuestGroupController+LeaveType";
    private const string Il2CppActionGenericTypeName = "Il2CppSystem.Action`1";
    private const string Il2CppActionTypeName = "Il2CppSystem.Action";
    private const string PartnerManagerTypeName = "NightScene.PartnerUtility.PartnerManager";
    private const string OrderChangeContextTypeName =
        "NightScene.PartnerUtility.PartnerManager+OrderChangeContext";
    private const string Il2CppObjectTypeName = "Il2CppSystem.Object";
    // 只保留最近的运行时订单，避免长时间经营或 Hook 异常时诊断缓存无限增长。
    private const int MaxOrders = 32;

    private static readonly object SyncRoot = new();
    private static readonly List<CapturedRuntimeSpecialOrder> Orders = new();
    private static readonly List<RuntimeParseFailureDiagnostic> RecentParseFailures = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly string[] RequiredPatchKeys =
    {
        PatchKey(GuestGroupControllerTypeName, "PushToOrder", 1, false),
        PatchKey(GuestsManagerTypeName, "SetManualControllerOrderInternal", 3, false),
        PatchKey(GuestsManagerTypeName, "EvaluateOrder", 3, false),
        PatchKey(GuestsManagerTypeName, "EvaulateManualOrder", 2, false),
        PatchKey(GuestsManagerTypeName, "CleanOrderInfo", 1, false),
        PatchKey(GuestsManagerTypeName, "RemoveFromOrder", 1, false),
        PatchKey(GuestsManagerTypeName, "RepellInternal", 4, false),
    };
    private static readonly string[] ObserverPatchKeys =
    {
        PatchKey(PartnerManagerTypeName, "OnOrderBaseStatusUpdate", 3, false),
        PatchKey(PartnerManagerTypeName, "NotifySystemChanged", 4, false),
    };
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static DateTime _lastAttachAttemptUtc = DateTime.MinValue;
    private static string _status = "not attached";
    private static long _firstCoveredBusinessGeneration = long.MaxValue;
    private static int _addCallbacks;
    private static int _removeCallbacks;
    private static int _manualCallbacks;
    private static int _statusCallbacks;
    private static int _capturedOrders;
    private static int _parseFailures;
    private static int _notApplicableCallbacks;
    private static long _changeVersion;
    private static string _lastCapture = "";
    private static string _lastParseFailure = "";
    private static string _lastNotApplicable = "";
    private static string _lastOrderShape = "";

    /// <summary>
    /// 捕获结果的变更版本号，供快照轮询方判断稀客订单是否需要重新发布。
    /// </summary>
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

    /// <summary>
    /// 返回当前 Hook 安装和解析状态，主要用于调试信息与本地 API 诊断。
    /// </summary>
    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return _status.StartsWith("error:", StringComparison.Ordinal)
                    ? _status
                    : BuildStatusLocked();
            }
        }
    }

    public static bool IsBusinessReady
    {
        get
        {
            lock (SyncRoot)
            {
                return RequiredPatchKeys.All(PatchedMethods.Contains)
                    && (!RuntimeNightBusinessLifecycle.IsActive
                        || RuntimeNightBusinessLifecycle.Generation >= _firstCoveredBusinessGeneration);
            }
        }
    }

    /// <summary>
    /// 尝试安装稀客订单相关的 Harmony Patch。
    /// </summary>
    /// <param name="log">用于输出首次安装、等待游戏类型加载或安装失败的 BepInEx 日志。</param>
    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(log, true);
    }

    /// <summary>
    /// 重置延迟重试时间，让下一次快照读取可以立刻尝试重新安装 Hook。
    /// </summary>
    public static void ResetAttachRetryDelay()
    {
        lock (SyncRoot)
        {
            _lastAttachAttemptUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 返回最近捕获到且未过期的稀客订单快照。
    /// </summary>
    /// <param name="maxAge">订单最后一次被捕获后允许保留的最长时间。</param>
    /// <returns>按首次捕获时间排序的订单副本，调用方可以安全枚举。</returns>
    /// <remarks>
    /// 读取快照时会顺带触发一次非强制 Attach。这样可以覆盖玩家进入经营场景后游戏类型才加载完成的情况，
    /// 但通过重试间隔避免每轮本地 API 轮询都反复扫描 AppDomain。
    /// </remarks>
    public static IReadOnlyList<CapturedRuntimeSpecialOrder> Snapshot(TimeSpan maxAge)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return Array.Empty<CapturedRuntimeSpecialOrder>();

        TryAttach(_log, false);
        var now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            var removed = Orders.RemoveAll(order => now - order.CapturedAt > maxAge);
            if (removed > 0)
            {
                _changeVersion++;
                _lastCapture = $"expired: count={removed}";
                _status = BuildStatusLocked();
            }

            return Orders
                .OrderBy(order => order.FirstCapturedAt)
                .ThenBy(order => order.CapturedAt)
                .ToList();
        }
    }

    /// <summary>
    /// 根据前端操作主动从捕获列表中移除一个订单。
    /// </summary>
    /// <param name="deskCode">游戏桌号，未知时为负数。</param>
    /// <param name="runtimeGuestId">稀客运行时原始 ID，未知时为 <c>null</c>。</param>
    /// <param name="foodTagId">料理原始 Tag ID，未指定时为 <c>null</c>。</param>
    /// <param name="beverageTagId">酒水原始 Tag ID，未指定时为 <c>null</c>。</param>
    /// <returns>被移除的捕获记录数量。</returns>
    public static int DismissOrder(int deskCode, int? runtimeGuestId, int? foodTagId, int? beverageTagId)
    {
        lock (SyncRoot)
        {
            var removed = Orders.RemoveAll(order => IsDismissRequestMatch(order, deskCode, runtimeGuestId, foodTagId, beverageTagId));
            _lastCapture = $"dismissed: desk={deskCode}, runtimeGuestId={runtimeGuestId?.ToString() ?? "missing"}, foodTagId={foodTagId?.ToString() ?? "missing"}, bevTagId={beverageTagId?.ToString() ?? "missing"}";
            if (removed > 0)
            {
                _changeVersion++;
            }

            _status = BuildStatusLocked();
            return removed;
        }
    }

    /// <summary>
    /// 清空内存中的稀客订单捕获结果。
    /// </summary>
    /// <param name="reason">记录到诊断状态中的清理原因。</param>
    public static void ClearOrders(string reason)
    {
        lock (SyncRoot)
        {
            if (Orders.Count > 0) _changeVersion++;
            Orders.Clear();
            _lastCapture = $"cleared: {reason}";
            _status = BuildStatusLocked();
        }
    }

    /// <summary>
    /// 返回近期订单解析失败诊断，帮助确认游戏版本字段变化或 Hook 参数形态变化。
    /// </summary>
    /// <param name="maxAge">诊断记录允许保留的最长时间。</param>
    /// <param name="limit">返回的最多记录数。</param>
    /// <returns>按时间倒序排列的简短诊断文本。</returns>
    public static IReadOnlyList<string> RecentParseFailuresSnapshot(TimeSpan maxAge, int limit = 16)
    {
        var now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            PruneRecentParseFailuresLocked(now, maxAge);
            return RecentParseFailures
                .OrderByDescending(failure => failure.CapturedAtUtc)
                .Take(limit)
                .Select(failure => $"{failure.CapturedAtUtc:O}; {failure.Message}")
                .ToList();
        }
    }

    /// <summary>
    /// 安装所有已知稀客订单生命周期 Hook。
    /// </summary>
    /// <remarks>
    /// 不同 DLC、普通订单和稀客手动订单会走不同游戏入口。这里允许缺失入口后续重试安装，
    /// 但只有全部权威创建与退出入口在经营开始前就绪时，捕获结果才会进入业务链路。
    /// </remarks>
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
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.special-order-runtime-capture");

            PatchMethod(
                _harmony,
                GuestGroupControllerTypeName,
                "PushToOrder",
                1,
                false,
                null,
                nameof(OnControllerOrderAdded),
                patchedNow,
                missing,
                exactMethodPredicate: IsExactOrderBaseMethod);
            PatchMethod(
                _harmony,
                GuestsManagerTypeName,
                "SetManualControllerOrderInternal",
                3,
                false,
                null,
                nameof(OnManualControllerOrderSet),
                patchedNow,
                missing,
                requireExactManualOrderSetter: true);
            PatchMethod(
                _harmony,
                GuestsManagerTypeName,
                "EvaluateOrder",
                3,
                false,
                nameof(CaptureControllerOrderBeforeCompletion),
                nameof(OnOrderCompletionSucceeded),
                patchedNow,
                missing,
                exactMethodPredicate: IsExactEvaluateOrder);
            PatchMethod(
                _harmony,
                GuestsManagerTypeName,
                "EvaulateManualOrder",
                2,
                false,
                nameof(CaptureControllerOrderBeforeCompletion),
                nameof(OnOrderCompletionSucceeded),
                patchedNow,
                missing,
                exactMethodPredicate: IsExactManualEvaluateOrder);
            PatchMethod(
                _harmony,
                GuestsManagerTypeName,
                "CleanOrderInfo",
                1,
                false,
                nameof(CaptureControllerOrderBeforeCompletion),
                nameof(OnOrderCleanupSucceeded),
                patchedNow,
                missing,
                exactMethodPredicate: IsExactControllerOnlyMethod);
            PatchMethod(
                _harmony,
                GuestsManagerTypeName,
                "RepellInternal",
                4,
                false,
                nameof(CaptureControllerOrderBeforeRepell),
                nameof(OnOrderRepellSucceeded),
                patchedNow,
                missing,
                exactMethodPredicate: IsExactRepellInternal);
            PatchMethod(
                _harmony,
                GuestsManagerTypeName,
                "RemoveFromOrder",
                1,
                false,
                nameof(CaptureOrderBeforeRemoval),
                nameof(OnOrderRemovalSucceeded),
                patchedNow,
                missing,
                exactMethodPredicate: IsExactOrderBaseMethod);
            PatchOptionalObserver(
                _harmony,
                "OnOrderBaseStatusUpdate",
                3,
                nameof(OnOrderStatusUpdated),
                patchedNow,
                missing,
                IsExactOrderStatusUpdate);
            PatchOptionalObserver(
                _harmony,
                "NotifySystemChanged",
                4,
                nameof(OnOrderSystemChanged),
                patchedNow,
                missing,
                IsExactNotifySystemChanged);

            lock (SyncRoot)
            {
                if (_firstCoveredBusinessGeneration == long.MaxValue
                    && RequiredPatchKeys.All(PatchedMethods.Contains))
                {
                    _firstCoveredBusinessGeneration = checked(RuntimeNightBusinessLifecycle.Generation + 1);
                }
                _status = BuildStatusLocked();
            }

            if (patchedNow.Count > 0)
            {
                log?.LogInfo($"Special order runtime capture patched: {string.Join(", ", patchedNow)}.");
            }
            else if (force && !IsBusinessReady)
            {
                log?.LogWarning($"Special order runtime capture waiting for game types: {string.Join(", ", missing.Take(4))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _status = $"error: {ex.Message}";
            }

            log?.LogWarning($"Special order runtime capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 为一个 IL2CPP 类型方法安装 Harmony Prefix 或 Postfix。
    /// </summary>
    /// <param name="harmony">当前捕获服务使用的 Harmony 实例。</param>
    /// <param name="typeName">游戏运行时类型全名。</param>
    /// <param name="methodName">需要监听的方法名。</param>
    /// <param name="parameterCount">用于区分重载的方法参数数量。</param>
    /// <param name="isStatic">目标方法是否为静态方法。</param>
    /// <param name="prefixName">本类中 Prefix 回调方法名，未使用时为 <c>null</c>。</param>
    /// <param name="postfixName">本类中 Postfix 回调方法名，未使用时为 <c>null</c>。</param>
    /// <param name="patchedNow">本次新安装成功的方法列表。</param>
    /// <param name="missing">当前游戏域尚未找到的方法或类型列表。</param>
    private static void PatchMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        bool isStatic,
        string? prefixName,
        string? postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing,
        bool requireExactManualOrderSetter = false,
        Func<MethodInfo, bool>? exactMethodPredicate = null)
    {
        var key = PatchKey(typeName, methodName, parameterCount, isStatic);
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = FindType(typeName);
        if (type == null)
        {
            missing.Add(typeName);
            return;
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var candidates = type
            .GetMethods(flags)
            .Where(method => method.Name == methodName && method.GetParameters().Length == parameterCount)
            .Where(method => !requireExactManualOrderSetter || IsExactManualOrderSetter(method))
            .Where(method => exactMethodPredicate == null || exactMethodPredicate(method))
            .ToArray();
        var requiresUniqueMatch = requireExactManualOrderSetter || exactMethodPredicate != null;
        var target = requiresUniqueMatch
            ? candidates.Length == 1 ? candidates[0] : null
            : candidates.FirstOrDefault();
        var prefix = prefixName == null ? null : typeof(SpecialOrderRuntimeCapture).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
        var postfix = postfixName == null ? null : typeof(SpecialOrderRuntimeCapture).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || (prefixName != null && prefix == null) || (postfixName != null && postfix == null))
        {
            missing.Add(key);
            return;
        }

        harmony.Patch(
            target,
            prefix: prefix == null ? null : new HarmonyMethod(prefix),
            postfix: postfix == null ? null : new HarmonyMethod(postfix));
        lock (SyncRoot)
        {
            PatchedMethods.Add(key);
        }

        patchedNow.Add(key);
    }

    private static void PatchOptionalObserver(
        Harmony harmony,
        string methodName,
        int parameterCount,
        string postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing,
        Func<MethodInfo, bool> exactMethodPredicate)
    {
        try
        {
            PatchMethod(
                harmony,
                PartnerManagerTypeName,
                methodName,
                parameterCount,
                false,
                null,
                postfixName,
                patchedNow,
                missing,
                exactMethodPredicate: exactMethodPredicate);
        }
        catch (Exception ex)
        {
            var diagnostic = $"{PartnerManagerTypeName}.{methodName}/{parameterCount}: {ex.GetBaseException().Message}";
            missing.Add(diagnostic);
            _log?.LogWarning($"Optional special-order status observer was not installed: {diagnostic}");
        }
    }

    private static bool IsExactManualOrderSetter(MethodInfo method)
    {
        if (method.ReturnType != typeof(void)) return false;

        var parameters = method.GetParameters();
        if (parameters.Length != 3
            || !string.Equals(
                parameters[0].ParameterType.FullName,
                GuestGroupControllerTypeName,
                StringComparison.Ordinal)
            || !string.Equals(
                parameters[2].ParameterType.FullName,
                RuntimeOrderTypeResolver.OrderBaseTypeName,
                StringComparison.Ordinal))
        {
            return false;
        }

        var callbackType = parameters[1].ParameterType;
        if (!callbackType.IsGenericType
            || !string.Equals(
                callbackType.GetGenericTypeDefinition().FullName,
                Il2CppActionGenericTypeName,
                StringComparison.Ordinal))
        {
            return false;
        }

        var callbackArguments = callbackType.GetGenericArguments();
        return callbackArguments.Length == 1
            && string.Equals(
                callbackArguments[0].FullName,
                EvaluationResultTypeName,
                StringComparison.Ordinal);
    }

    private static bool IsExactOrderBaseMethod(MethodInfo method)
    {
        return HasExactVoidSignature(method, RuntimeOrderTypeResolver.OrderBaseTypeName);
    }

    private static bool IsExactControllerOnlyMethod(MethodInfo method)
    {
        return HasExactVoidSignature(method, GuestGroupControllerTypeName);
    }

    private static bool IsExactEvaluateOrder(MethodInfo method)
    {
        return HasExactVoidSignature(
            method,
            GuestGroupControllerTypeName,
            typeof(bool).FullName!,
            Il2CppActionTypeName);
    }

    private static bool IsExactManualEvaluateOrder(MethodInfo method)
    {
        if (method.ReturnType != typeof(void)) return false;
        var parameters = method.GetParameters();
        if (parameters.Length != 2
            || !string.Equals(parameters[0].ParameterType.FullName, GuestGroupControllerTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        var callbackType = parameters[1].ParameterType;
        if (!callbackType.IsGenericType
            || !string.Equals(callbackType.GetGenericTypeDefinition().FullName, Il2CppActionGenericTypeName, StringComparison.Ordinal))
        {
            return false;
        }

        var arguments = callbackType.GetGenericArguments();
        return arguments.Length == 1
            && string.Equals(arguments[0].FullName, EvaluationResultTypeName, StringComparison.Ordinal);
    }

    private static bool IsExactRepellInternal(MethodInfo method)
    {
        if (method.ReturnType != typeof(void)) return false;
        var parameters = method.GetParameters();
        return parameters.Length == 4
            && string.Equals(parameters[0].ParameterType.FullName, GuestGroupControllerTypeName, StringComparison.Ordinal)
            && parameters[1].IsOut
            && parameters[1].ParameterType.IsByRef
            && parameters[1].ParameterType.GetElementType() == typeof(bool)
            && string.Equals(parameters[2].ParameterType.FullName, LeaveTypeName, StringComparison.Ordinal)
            && parameters[3].ParameterType == typeof(bool);
    }

    private static bool IsExactOrderStatusUpdate(MethodInfo method)
    {
        return HasExactVoidSignature(
            method,
            RuntimeOrderTypeResolver.OrderBaseTypeName,
            OrderChangeContextTypeName,
            typeof(int).FullName!);
    }

    private static bool IsExactNotifySystemChanged(MethodInfo method)
    {
        return HasExactVoidSignature(
            method,
            typeof(int).FullName!,
            OrderChangeContextTypeName,
            RuntimeOrderTypeResolver.OrderBaseTypeName,
            Il2CppObjectTypeName);
    }

    private static string PatchKey(string typeName, string methodName, int parameterCount, bool isStatic)
    {
        return $"{typeName}.{methodName}/{parameterCount}/{(isStatic ? "static" : "instance")}";
    }

    private static bool HasExactVoidSignature(MethodInfo method, params string[] parameterTypeNames)
    {
        if (method.ReturnType != typeof(void)) return false;
        var parameters = method.GetParameters();
        if (parameters.Length != parameterTypeNames.Length) return false;

        for (var index = 0; index < parameters.Length; index++)
        {
            if (!string.Equals(parameters[index].ParameterType.FullName, parameterTypeNames[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void OnControllerOrderAdded(object __instance, object __0, bool __runOriginal)
    {
        if (!__runOriginal) return;

        RunCaptureCallback("ControllerOrderAdd", () =>
        {
            lock (SyncRoot) _addCallbacks++;
            AddOrder(ParseOrder(__0, "ControllerOrderAdd", __instance));
        });
    }

    private static void CaptureOrderBeforeRemoval(object __0, out CapturedRuntimeSpecialOrder? __state)
    {
        CapturedRuntimeSpecialOrder? state = null;
        RunCaptureCallback("OrderRemove.Before", () =>
        {
            state = ParseOrder(__0, "OrderRemove.Before");
        });
        __state = state;
    }

    private static void OnOrderRemovalSucceeded(CapturedRuntimeSpecialOrder? __state, bool __runOriginal)
    {
        if (!__runOriginal || __state == null) return;

        RunCaptureCallback("OrderRemove.After", () =>
        {
            lock (SyncRoot) _removeCallbacks++;
            RemoveOrder(__state with { CaptureSource = "OrderRemove" });
        });
    }

    private static void OnOrderStatusUpdated(object __0, object __1)
    {
        RunCaptureCallback("OrderStatusUpdate", () =>
        {
            lock (SyncRoot) _statusCallbacks++;
            UpdateOrderStatus(ParseOrder(__0, "OrderStatusUpdate"), __1);
        });
    }

    private static void OnOrderSystemChanged(object __0, object __1, object __2)
    {
        RunCaptureCallback("OrderSystemChanged", () =>
        {
            lock (SyncRoot) _statusCallbacks++;
            if (__2 == null)
            {
                NoteNotApplicable("OrderSystemChanged", "null order");
                return;
            }
            UpdateOrderStatus(ParseOrder(__2, "OrderSystemChanged"), __1);
        });
    }

    private static void OnManualControllerOrderSet(
        object __0,
        object? __1,
        object __2,
        bool __runOriginal)
    {
        if (!__runOriginal) return;

        RunCaptureCallback("ManualOrderSet", () =>
        {
            lock (SyncRoot) _manualCallbacks++;
            var order = ParseOrder(__2, "ManualOrderSet", __0);
            if (order is not { ManualOrder: true })
            {
                NoteParseFailure("ManualOrderSet", "OrderBase.ManualOrder was not true after the native setter", __2);
                return;
            }

            if (__1 == null)
            {
                NoteParseFailure("ManualOrderSet", "manual evaluation callback is null", __2);
            }

            AddOrder(order with
            {
                ManualEvaluationCallback = __1,
                ManualEvaluationBindingObserved = true,
                ManualEvaluationBindingCallback = __1,
            });
        });
    }

    private static void CaptureControllerOrderBeforeCompletion(
        object __0,
        MethodBase __originalMethod,
        out CapturedRuntimeSpecialOrder? __state)
    {
        CapturedRuntimeSpecialOrder? state = null;
        var source = $"{__originalMethod.Name}.Before";
        RunCaptureCallback(source, () =>
        {
            state = ParseControllerCurrentOrder(__0, source);
        });
        __state = state;
    }

    private static void OnOrderCompletionSucceeded(
        CapturedRuntimeSpecialOrder? __state,
        MethodBase __originalMethod,
        bool __runOriginal)
    {
        if (!__runOriginal || __state is not { IsFulfilled: true }) return;

        RunCaptureCallback($"{__originalMethod.Name}.After", () =>
        {
            lock (SyncRoot) _removeCallbacks++;
            RemoveOrder(__state with { CaptureSource = __originalMethod.Name });
        });
    }

    private static void OnOrderCleanupSucceeded(
        CapturedRuntimeSpecialOrder? __state,
        MethodBase __originalMethod,
        bool __runOriginal)
    {
        if (!__runOriginal || __state == null) return;

        RunCaptureCallback($"{__originalMethod.Name}.After", () =>
        {
            lock (SyncRoot) _removeCallbacks++;
            RemoveOrder(__state with { CaptureSource = __originalMethod.Name });
        });
    }

    private static void CaptureControllerOrderBeforeRepell(
        object __0,
        MethodBase __originalMethod,
        out CapturedRuntimeSpecialOrder? __state)
    {
        CaptureControllerOrderBeforeCompletion(__0, __originalMethod, out __state);
    }

    private static void OnOrderRepellSucceeded(
        CapturedRuntimeSpecialOrder? __state,
        MethodBase __originalMethod,
        bool __runOriginal)
    {
        if (!__runOriginal || __state == null) return;
        OnOrderCleanupSucceeded(__state, __originalMethod, true);
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
            NoteParseFailure(source, $"capture callback failed: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// 合并一条新捕获的订单记录。
    /// </summary>
    /// <remarks>
    /// 同一稀客订单可能先由控制器绑定捕获，后续再由状态回调补充送达信息。
    /// 因此这里按运行时对象、桌号和稀客维度合并，并优先保留信息更完整的一份记录。
    /// </remarks>
    private static void AddOrder(CapturedRuntimeSpecialOrder? order)
    {
        if (order == null) return;
        if (!HasExactControllerBinding(order))
        {
            NoteParseFailure(
                order.CaptureSource,
                "native order/controller binding is incomplete",
                order.OrderObject);
            return;
        }

        lock (SyncRoot)
        {
            var existing = Orders.Where(current => CanMergeCapturedOrders(current, order)).ToList();
            Orders.RemoveAll(current => CanMergeCapturedOrders(current, order));

            var next = existing.Aggregate(order, MergeCapturedOrder);
            Orders.Add(next);
            _capturedOrders++;
            _lastCapture = $"{next.CaptureSource}: desk={next.DeskCode}, guestId={next.GuestId?.ToString() ?? ""}, foodId={(next.HasFoodTagId ? next.FoodTagId.ToString() : "missing")}, foodText={next.FoodTagDisplayText}, bevId={(next.HasBeverageTagId ? next.BeverageTagId.ToString() : "missing")}, bevText={next.BeverageTagDisplayText}, free={next.IsFreeOrder}, manual={next.ManualOrder}, manualCallback={(next.ManualEvaluationCallback == null ? "no" : "yes")}, manualBinding={DescribeManualEvaluationBinding(next.ManualEvaluationBindingObserved, next.ManualEvaluationBindingConflict, next.ManualEvaluationBindingCallback)}";
            _changeVersion++;
            if (Orders.Count > MaxOrders)
            {
                Orders.RemoveRange(0, Orders.Count - MaxOrders);
            }

            _status = BuildStatusLocked();
        }
    }

    /// <summary>
    /// 根据游戏移除、评价完成或手动结束回调移除订单。
    /// </summary>
    private static void RemoveOrder(CapturedRuntimeSpecialOrder? order)
    {
        if (order == null) return;
        if (string.IsNullOrWhiteSpace(order.RuntimeKey))
        {
            NoteParseFailure(order.CaptureSource, "native order key is unavailable for removal", order.OrderObject);
            return;
        }

        lock (SyncRoot)
        {
            var removed = Orders.RemoveAll(existing => IsSameOrderRemovalMatch(existing, order));
            _lastCapture = $"removed: desk={order.DeskCode}, guestId={order.GuestId?.ToString() ?? ""}";
            if (removed > 0) _changeVersion++;
            _status = BuildStatusLocked();
        }
    }

    /// <summary>
    /// 处理订单状态更新回调：明确移除时清理，送达完成时刷新并保留捕获记录。
    /// </summary>
    /// <remarks>
    /// 游戏部分状态更新只表示 UI 或伙伴系统刷新，不代表订单结束。`IsFullfilled` 仍需进入评价阶段，
    /// 因此不能把料理和酒水均已送达当作订单移除。
    /// </remarks>
    private static void UpdateOrderStatus(CapturedRuntimeSpecialOrder? order, object? context)
    {
        if (order == null) return;

        var contextName = FormatValue(context);
        if (string.Equals(contextName, "OrderRemove", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextName, "2", StringComparison.Ordinal))
        {
            RemoveOrder(order with { CaptureSource = "OrderRemove" });
            return;
        }

        if (!IsOrderDeliveryContext(contextName)) return;
        if (!order.IsFulfilled) return;

        // IsFullfilled only means that food and beverage were served. The order must remain
        // available for the completion/evaluation stage until an explicit removal callback.
        UpdateExistingOrder(order with { CaptureSource = MergeCaptureSource(order.CaptureSource, "Fulfilled") });
    }

    private static void UpdateExistingOrder(CapturedRuntimeSpecialOrder order)
    {
        lock (SyncRoot)
        {
            var existing = Orders.Where(current => CanMergeCapturedOrders(current, order)).ToList();
            if (existing.Count == 0) return;

            Orders.RemoveAll(current => CanMergeCapturedOrders(current, order));
            var next = existing.Aggregate(order, MergeCapturedOrder);
            if (!HasExactControllerBinding(next))
            {
                Orders.AddRange(existing);
                NoteParseFailure(order.CaptureSource, "status update lost the confirmed controller binding", order.OrderObject);
                return;
            }

            Orders.Add(next);
            _capturedOrders++;
            _lastCapture = $"updated:{next.CaptureSource}: runtimeKey={next.RuntimeKey}";
            _changeVersion++;
            _status = BuildStatusLocked();
        }
    }

    private static bool HasExactControllerBinding(CapturedRuntimeSpecialOrder order)
    {
        return order.OrderObject != null
            && order.ControllerObject != null
            && !string.IsNullOrWhiteSpace(order.RuntimeKey);
    }

    private static bool IsOrderDeliveryContext(string contextName)
    {
        return string.Equals(contextName, "FoodDelivered", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextName, "BeverageDelivered", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextName, "3", StringComparison.Ordinal)
            || string.Equals(contextName, "4", StringComparison.Ordinal);
    }

    /// <summary>
    /// 从运行时订单对象和可选控制器中解析稀客、桌号、原始 Tag ID 与最终展示文本。
    /// </summary>
    /// <param name="order">游戏订单对象，可能是 IL2CPP 基类或具体 SpecialOrder。</param>
    /// <param name="source">触发解析的 Hook 名称，用于诊断。</param>
    /// <param name="controller">订单所属控制器，部分字段只能从控制器读取。</param>
    /// <returns>解析成功时返回标准捕获记录；非稀客订单或字段不足时返回 <c>null</c>。</returns>
    private static CapturedRuntimeSpecialOrder? ParseOrder(object? order, string source, object? controller = null)
    {
        if (order == null)
        {
            NoteParseFailure(source, "order is null");
            return null;
        }

        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (!resolution.RecognizedOrderType)
        {
            NoteNotApplicable(source, order.GetType().FullName ?? order.GetType().Name);
            return null;
        }

        if (!resolution.Resolved)
        {
            NoteParseFailure(source, resolution.Reason, order);
            return null;
        }

        if (resolution.Kind != RuntimeOrderKind.Special || resolution.ReadableOrder == null)
        {
            NoteNotApplicable(source, resolution.KindName);
            return null;
        }

        var readableOrder = resolution.ReadableOrder;
        var hasExactManualOrder = TryReadExactManualOrder(readableOrder, out var manualOrder);
        if (!hasExactManualOrder)
        {
            NoteParseFailure(source, "exact OrderBase.ManualOrder bool property is unavailable", readableOrder);
            return null;
        }

        if (!TryReadExactOrderBool(readableOrder, "FreeOrder", out var isFreeOrder))
        {
            NoteParseFailure(source, "exact OrderBase.FreeOrder bool property is unavailable", readableOrder);
            return null;
        }
        if (!TryReadExactOrderBool(readableOrder, "IsFullfilled", out var isFulfilled))
        {
            NoteParseFailure(source, "exact OrderBase.IsFullfilled bool property is unavailable", readableOrder);
            return null;
        }

        var specialGuest = GetMemberValue(readableOrder, "SpecialGuests")
            ?? GetMemberValue(controller, "SpecialGuest")
            ?? GetMemberValue(controller, "OrderingGuest");
        var foodTagId = ToNullableInt(GetMemberValue(readableOrder, "RequestFoodTag"));
        var beverageTagId = ToNullableInt(GetMemberValue(readableOrder, "RequestBeverageTag"));
        var deskCode = ToNullableInt(GetMemberValue(readableOrder, "DeskCode"))
            ?? ToNullableInt(GetMemberValue(controller, "DeskCode"))
            ?? -1;
        return ParseSpecialOrderParts(
            specialGuest,
            foodTagId,
            beverageTagId,
            deskCode,
            isFreeOrder,
            manualOrder,
            isFulfilled,
            readableOrder,
            controller,
            source);
    }

    private static CapturedRuntimeSpecialOrder? ParseSpecialOrderParts(
        object? specialGuest,
        int? foodTagId,
        int? beverageTagId,
        int deskCode,
        bool isFreeOrder,
        bool manualOrder,
        bool isFulfilled,
        object? order,
        object? controller,
        string source)
    {
        var guestId = ToNullableInt(GetMemberValue(specialGuest, "Id"));
        var foodTagDisplayText = ResolveOrderTagText(controller, order, "GetOrderFoodText");
        var beverageTagDisplayText = ResolveOrderTagText(controller, order, "GetOrderBevText");

        var guestName = specialGuest == null ? "" : ReadGuestName(specialGuest, guestId);

        if (!foodTagId.HasValue
            && !beverageTagId.HasValue
            && string.IsNullOrWhiteSpace(foodTagDisplayText)
            && string.IsNullOrWhiteSpace(beverageTagDisplayText))
        {
            NoteParseFailure(source, "empty food/beverage tag", order);
            return null;
        }

        if (string.IsNullOrWhiteSpace(guestName) && specialGuest == null)
        {
            NoteParseFailure(source, "special guest missing", order);
            return null;
        }

        var capturedAt = DateTime.UtcNow;
        return new CapturedRuntimeSpecialOrder(
            deskCode,
            guestId,
            string.IsNullOrWhiteSpace(guestName) ? "Special guest" : guestName,
            foodTagId ?? 0,
            foodTagId.HasValue,
            foodTagDisplayText,
            beverageTagId ?? 0,
            beverageTagId.HasValue,
            beverageTagDisplayText,
            isFreeOrder,
            isFulfilled,
            capturedAt,
            capturedAt,
            GetRuntimeObjectKey(order),
            source)
        {
            OrderObject = order,
            ControllerObject = controller,
            ManualOrder = manualOrder,
        };
    }

    /// <summary>
    /// 读取游戏针对当前订单最终展示的 Tag 文本。
    /// </summary>
    /// <remarks>
    /// 稀客订单允许使用不在全局 Tag 目录中的 ID（例如无酒精为 -1），控制器方法还会应用
    /// 订单文本 override，因此它是有控制器上下文时的唯一展示文本来源。订单原始 ID 独立保留，
    /// 无控制器时不调用订单文本 getter 或 <c>ToString()</c> 猜测展示内容。
    /// </remarks>
    private static string ResolveOrderTagText(
        object? controller,
        object? order,
        string controllerMethodName)
    {
        return NormalizeTag(InvokeInstanceMethod(controller, controllerMethodName, order)?.ToString());
    }

    /// <summary>
    /// 在原生完成调用前，从控制器当前订单锁存精确稀客订单身份。
    /// </summary>
    private static CapturedRuntimeSpecialOrder? ParseControllerCurrentOrder(object? controller, string source)
    {
        if (controller == null)
        {
            NoteParseFailure(source, "controller is null");
            return null;
        }

        var peekOrder = InvokeInstanceMethod(controller, "PeekOrders");
        if (peekOrder != null)
        {
            var parsed = ParseOrder(peekOrder, source, controller);
            if (parsed != null) return parsed;
        }

        NoteParseFailure(source, "exact current SpecialOrder is unavailable", controller);
        return null;
    }

    /// <summary>
    /// 判断两个捕获记录是否指向同一个原生订单。
    /// </summary>
    /// <remarks>
    /// 捕获创建已经强制要求原生指针；指针缺失时必须拒绝，不能按桌号或稀客身份重绑。
    /// </remarks>
    private static bool IsSameOrderSlot(CapturedRuntimeSpecialOrder left, CapturedRuntimeSpecialOrder right)
    {
        return !string.IsNullOrWhiteSpace(left.RuntimeKey)
            && !string.IsNullOrWhiteSpace(right.RuntimeKey)
            && string.Equals(left.RuntimeKey, right.RuntimeKey, StringComparison.Ordinal);
    }

    private static bool CanMergeCapturedOrders(CapturedRuntimeSpecialOrder left, CapturedRuntimeSpecialOrder right)
    {
        return IsSameOrderSlot(left, right) && CanMergeCapturedOrderDetails(left, right);
    }

    private static bool IsSameOrderRemovalMatch(CapturedRuntimeSpecialOrder existing, CapturedRuntimeSpecialOrder removed)
    {
        return IsSameOrderSlot(existing, removed);
    }

    private static bool IsDismissRequestMatch(
        CapturedRuntimeSpecialOrder existing,
        int deskCode,
        int? runtimeGuestId,
        int? foodTagId,
        int? beverageTagId)
    {
        if (deskCode < 0 || existing.DeskCode != deskCode) return false;
        if (!runtimeGuestId.HasValue && !foodTagId.HasValue && !beverageTagId.HasValue) return false;
        if (runtimeGuestId.HasValue
            && (!existing.GuestId.HasValue || existing.GuestId.Value != runtimeGuestId.Value)) return false;
        if (foodTagId.HasValue
            && (!existing.HasFoodTagId || existing.FoodTagId != foodTagId.Value)) return false;
        if (beverageTagId.HasValue
            && (!existing.HasBeverageTagId || existing.BeverageTagId != beverageTagId.Value)) return false;
        return true;
    }

    /// <summary>
    /// 合并两次 Hook 捕获到的同一订单信息。
    /// </summary>
    /// <remarks>
    /// 不同来源的记录可能分别拥有展示文本、原始 Tag ID、稀客 ID 或控制器对象。合并时独立保留
    /// 原始身份与展示文本；原始 ID 明确冲突时，不把两条记录视为同一订单。
    /// </remarks>
    private static CapturedRuntimeSpecialOrder MergeCapturedOrder(
        CapturedRuntimeSpecialOrder incoming,
        CapturedRuntimeSpecialOrder existing)
    {
        if (!CanMergeCapturedOrderDetails(incoming, existing))
        {
            return GetCapturedOrderCompletenessScore(incoming) >= GetCapturedOrderCompletenessScore(existing)
                ? incoming
                : existing with { CapturedAt = incoming.CapturedAt };
        }

        var food = MergeTagParts(
            incoming.FoodTagId,
            incoming.HasFoodTagId,
            incoming.FoodTagDisplayText,
            existing.FoodTagId,
            existing.HasFoodTagId,
            existing.FoodTagDisplayText);
        var beverage = MergeTagParts(
            incoming.BeverageTagId,
            incoming.HasBeverageTagId,
            incoming.BeverageTagDisplayText,
            existing.BeverageTagId,
            existing.HasBeverageTagId,
            existing.BeverageTagDisplayText);

        var manualBindingConflict = incoming.ManualEvaluationBindingConflict
            || existing.ManualEvaluationBindingConflict
            || HaveConflictingManualEvaluationBindings(incoming, existing);
        var manualBindingCallback = incoming.ManualEvaluationBindingObserved
            ? incoming.ManualEvaluationBindingCallback ?? existing.ManualEvaluationBindingCallback
            : existing.ManualEvaluationBindingCallback;

        return incoming with
        {
            GuestId = incoming.GuestId ?? existing.GuestId,
            GuestName = string.IsNullOrWhiteSpace(incoming.GuestName) || string.Equals(incoming.GuestName, "Special guest", StringComparison.Ordinal)
                ? existing.GuestName
                : incoming.GuestName,
            FoodTagId = food.TagId,
            HasFoodTagId = food.HasTagId,
            FoodTagDisplayText = food.DisplayText,
            BeverageTagId = beverage.TagId,
            HasBeverageTagId = beverage.HasTagId,
            BeverageTagDisplayText = beverage.DisplayText,
            IsFreeOrder = incoming.IsFreeOrder || existing.IsFreeOrder,
            IsFulfilled = incoming.IsFulfilled || existing.IsFulfilled,
            FirstCapturedAt = existing.FirstCapturedAt < incoming.FirstCapturedAt ? existing.FirstCapturedAt : incoming.FirstCapturedAt,
            RuntimeKey = string.IsNullOrWhiteSpace(incoming.RuntimeKey) ? existing.RuntimeKey : incoming.RuntimeKey,
            CaptureSource = MergeCaptureSource(existing.CaptureSource, incoming.CaptureSource),
            OrderObject = incoming.OrderObject ?? existing.OrderObject,
            ControllerObject = incoming.ControllerObject ?? existing.ControllerObject,
            ManualOrder = incoming.ManualOrder,
            ManualEvaluationCallback = incoming.ManualOrder
                ? incoming.ManualEvaluationCallback
                    ?? (existing.ManualOrder ? existing.ManualEvaluationCallback : null)
                : null,
            ManualEvaluationBindingObserved = incoming.ManualEvaluationBindingObserved
                || existing.ManualEvaluationBindingObserved,
            ManualEvaluationBindingConflict = manualBindingConflict,
            ManualEvaluationBindingCallback = manualBindingCallback,
        };
    }

    private static bool HaveConflictingManualEvaluationBindings(
        CapturedRuntimeSpecialOrder incoming,
        CapturedRuntimeSpecialOrder existing)
    {
        if (!incoming.ManualEvaluationBindingObserved
            || !existing.ManualEvaluationBindingObserved)
        {
            return false;
        }

        var incomingCallback = incoming.ManualEvaluationBindingCallback;
        var existingCallback = existing.ManualEvaluationBindingCallback;
        if (incomingCallback == null || existingCallback == null)
        {
            return incomingCallback != null || existingCallback != null;
        }

        if (ReferenceEquals(incomingCallback, existingCallback)) return false;
        return !RuntimeReflectionUtility.TryReadNativeObjectPointer(incomingCallback, out var incomingPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(existingCallback, out var existingPointer)
            || incomingPointer == 0
            || existingPointer == 0
            || incomingPointer != existingPointer;
    }

    private static string DescribeManualEvaluationBinding(
        bool observed,
        bool conflict,
        object? callback)
    {
        if (conflict) return "conflict";
        if (!observed) return "absent";
        return callback == null ? "invalid" : "captured";
    }

    private static bool CanMergeCapturedOrderDetails(CapturedRuntimeSpecialOrder left, CapturedRuntimeSpecialOrder right)
    {
        if (!string.IsNullOrWhiteSpace(left.RuntimeKey)
            && !string.IsNullOrWhiteSpace(right.RuntimeKey)
            && string.Equals(left.RuntimeKey, right.RuntimeKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (HaveConflictingTagIds(left.HasFoodTagId, left.FoodTagId, right.HasFoodTagId, right.FoodTagId))
        {
            return false;
        }

        if (HaveConflictingTagIds(left.HasBeverageTagId, left.BeverageTagId, right.HasBeverageTagId, right.BeverageTagId))
        {
            return false;
        }

        return true;
    }

    private static bool HaveConflictingTagIds(
        bool leftHasTagId,
        int leftTagId,
        bool rightHasTagId,
        int rightTagId)
    {
        return leftHasTagId && rightHasTagId && leftTagId != rightTagId;
    }

    private static (int TagId, bool HasTagId, string DisplayText) MergeTagParts(
        int incomingTagId,
        bool incomingHasTagId,
        string incomingDisplayText,
        int existingTagId,
        bool existingHasTagId,
        string existingDisplayText)
    {
        var hasTagId = incomingHasTagId || existingHasTagId;
        var tagId = incomingHasTagId ? incomingTagId : existingTagId;
        var displayText = string.IsNullOrWhiteSpace(incomingDisplayText)
            ? existingDisplayText
            : incomingDisplayText;
        return (tagId, hasTagId, displayText);
    }

    private static int GetCapturedOrderCompletenessScore(CapturedRuntimeSpecialOrder order)
    {
        return GetTagCompletenessScore(order.HasFoodTagId, order.FoodTagDisplayText)
            + GetTagCompletenessScore(order.HasBeverageTagId, order.BeverageTagDisplayText)
            + (order.GuestId.HasValue ? 2 : 0)
            + (order.DeskCode >= 0 ? 1 : 0);
    }

    private static int GetTagCompletenessScore(bool hasTagId, string displayText)
    {
        return (!string.IsNullOrWhiteSpace(displayText) ? 8 : 0) + (hasTagId ? 2 : 0);
    }

    private static string MergeCaptureSource(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing)) return incoming;
        if (string.IsNullOrWhiteSpace(incoming)) return existing;
        if (string.Equals(existing, incoming, StringComparison.Ordinal)) return incoming;
        return $"{existing}+{incoming}";
    }

    private static bool TryReadExactManualOrder(object order, out bool manualOrder)
    {
        return TryReadExactOrderBool(order, "ManualOrder", out manualOrder);
    }

    private static bool TryReadExactOrderBool(object order, string propertyName, out bool result)
    {
        result = false;
        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        for (var type = order.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(propertyName, flags);
            if (property == null) continue;
            if (property.PropertyType != typeof(bool)) return false;

            try
            {
                if (property.GetValue(order) is not bool value) return false;
                result = value;
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static string ReadGuestName(object specialGuest, int? guestId)
    {
        var stringId = GetMemberValue(specialGuest, "StringId")?.ToString();
        if (!string.IsNullOrWhiteSpace(stringId)) return stringId;
        return guestId.HasValue ? $"Guest {guestId.Value}" : "Special guest";
    }

    private static string NormalizeTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "Null", StringComparison.OrdinalIgnoreCase)) return "";
        if (string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase)) return "";
        return trimmed.StartsWith("#", StringComparison.Ordinal) ? "" : trimmed;
    }

    private static object? InvokeInstanceMethod(object? instance, string name, params object?[] args)
    {
        if (instance == null) return null;

        var method = instance
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
        if (method == null) return null;

        try
        {
            return method.Invoke(instance, args);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 兼容读取 IL2CPP 自动属性、私有字段和常见字段命名风格。
    /// </summary>
    /// <remarks>
    /// Il2CppInterop 暴露的对象在不同版本或反编译形态下可能出现 <c>m_</c>、下划线和 backing field 等命名。
    /// 统一在这里收敛字段候选，避免业务解析逻辑里散落多套反射访问。
    /// </remarks>
    private static object? GetMemberValue(object? instance, string name)
    {
        if (instance == null) return null;
        var type = instance.GetType();

        while (type != null)
        {
            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (TryReadField(instance, field, out var fieldValue) && fieldValue != null) return fieldValue;
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out var propertyValue)) return propertyValue;

            var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
            property = type.GetProperty(pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out propertyValue)) return propertyValue;

            type = type.BaseType;
        }

        return null;
    }

    /// <summary>
    /// 记录一次订单解析失败，并保存经过截断的对象形态诊断。
    /// </summary>
    /// <remarks>
    /// 解析失败不抛出异常，避免 Hook 影响游戏原始流程；诊断通过本地 API 暴露给调试面板。
    /// </remarks>
    private static void NoteParseFailure(string source, string reason, object? order = null)
    {
        var shape = DescribeOrderShape(order);
        lock (SyncRoot)
        {
            _parseFailures++;
            _lastParseFailure = $"{source}: {reason}";
            _lastOrderShape = shape;
            AddRecentParseFailureLocked(source, reason, shape);
            _status = BuildStatusLocked();
        }
    }

    private static void NoteNotApplicable(string source, string orderTypeName)
    {
        lock (SyncRoot)
        {
            _notApplicableCallbacks++;
            _lastNotApplicable = $"{source}: {orderTypeName}";
            _status = BuildStatusLocked();
        }
    }

    private static void AddRecentParseFailureLocked(string source, string reason, string shape)
    {
        RecentParseFailures.Add(new RuntimeParseFailureDiagnostic(
            DateTime.UtcNow,
            TrimStatus($"{source}: {reason}; {shape}", 900)));
        PruneRecentParseFailuresLocked(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        if (RecentParseFailures.Count > 40)
        {
            RecentParseFailures.RemoveRange(0, RecentParseFailures.Count - 40);
        }
    }

    private static void PruneRecentParseFailuresLocked(DateTime nowUtc, TimeSpan maxAge)
    {
        RecentParseFailures.RemoveAll(failure => nowUtc - failure.CapturedAtUtc > maxAge);
    }

    private static string BuildStatusLocked()
    {
        var missing = RequiredPatchKeys.Where(key => !PatchedMethods.Contains(key)).Select(ShortPatchKey).ToArray();
        var observers = ObserverPatchKeys.Count(PatchedMethods.Contains);
        var covered = missing.Length == 0
            && (!RuntimeNightBusinessLifecycle.IsActive
                || RuntimeNightBusinessLifecycle.Generation >= _firstCoveredBusinessGeneration);
        return $"ready={(covered ? "yes" : "no")}; required={RequiredPatchKeys.Length - missing.Length}/{RequiredPatchKeys.Length}; observers={observers}/{ObserverPatchKeys.Length}; firstCoveredGeneration={(_firstCoveredBusinessGeneration == long.MaxValue ? "pending" : _firstCoveredBusinessGeneration)}; currentGeneration={RuntimeNightBusinessLifecycle.Generation}; missing=[{string.Join(",", missing)}]; patched={PatchedMethods.Count}; version={_changeVersion}; callbacks=add:{_addCallbacks},remove:{_removeCallbacks},manual:{_manualCallbacks},statusUpdate:{_statusCallbacks}; captured={_capturedOrders}; bound={Orders.Count}; notApplicable={_notApplicableCallbacks}; parseFailures={_parseFailures}; lastCapture={_lastCapture}; lastNotApplicable={_lastNotApplicable}; lastParseFailure={_lastParseFailure}; lastOrderShape={_lastOrderShape}";
    }

    private static string ShortPatchKey(string key)
    {
        var separator = key.LastIndexOf('.');
        return separator >= 0 ? key[(separator + 1)..] : key;
    }

    private static string DescribeOrderShape(object? order)
    {
        if (order == null) return "";

        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        var readableOrder = resolution.ReadableOrder ?? order;
        var parts = new List<string>
        {
            $"type={order.GetType().FullName}",
            $"resolved={resolution.Resolved}:{resolution.KindName}:{resolution.Reason}",
            $"Type={ShortValue(GetMemberValue(readableOrder, "Type"))}",
            $"DeskCode={ShortValue(GetMemberValue(readableOrder, "DeskCode"))}",
            $"RequestFoodTag={ShortValue(GetMemberValue(readableOrder, "RequestFoodTag"))}",
            $"RequestBeverageTag={ShortValue(GetMemberValue(readableOrder, "RequestBeverageTag"))}",
            $"SpecialGuests={ShortValue(GetMemberValue(readableOrder, "SpecialGuests"))}",
        };

        return TrimStatus(string.Join("|", parts), 700);
    }

    private static string ShortValue(object? value)
    {
        return TrimStatus(FormatValue(value), 80);
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        if (value is string stringValue) return stringValue;
        var type = value.GetType();
        if (type.IsEnum || type.IsPrimitive || value is decimal) return value.ToString() ?? "";
        try
        {
            if (value is IConvertible convertible) return convertible.ToInt32(null).ToString();
        }
        catch
        {
            // 无法转换为数字时继续使用类型名，避免诊断文本因为异常中断。
        }

        return type.FullName ?? type.Name;
    }

    private static string TrimStatus(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static IEnumerable<string> BuildFieldNameCandidates(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;

        yield return name;
        yield return $"<{name}>k__BackingField";
        yield return $"m_{name}";
        yield return $"_{name}";

        var camelName = char.ToLowerInvariant(name[0]) + name[1..];
        if (!string.Equals(camelName, name, StringComparison.Ordinal))
        {
            yield return camelName;
            yield return $"<{camelName}>k__BackingField";
            yield return $"m_{camelName}";
            yield return $"_{camelName}";
        }
    }

    private static bool TryReadProperty(object? instance, PropertyInfo? property, out object? value)
    {
        value = null;
        if (property == null) return false;

        try
        {
            value = property.GetValue(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadField(object? instance, FieldInfo? field, out object? value)
    {
        value = null;
        if (field == null) return false;

        try
        {
            value = field.GetValue(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetRuntimeObjectKey(object? value)
    {
        return RuntimeReflectionUtility.TryReadNativeObjectPointer(value, out var pointer)
            ? $"ptr:{pointer:x}"
            : "";
    }

    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch
            {
                // 某些程序集解析无关 IL2CPP 类型时会抛异常，查找目标类型时直接跳过即可。
            }
        }

        return null;
    }

    private static int? ToNullableInt(object? value)
    {
        if (value == null) return null;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        try
        {
            if (value is IConvertible convertible) return convertible.ToInt32(null);
        }
        catch
        {
            // 无法按 IConvertible 读取时继续尝试字符串解析。
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

}

/// <summary>
/// 一条从游戏运行时捕获到的稀客订单。
/// </summary>
/// <remarks>
/// 原始请求 Tag ID 用于订单身份匹配，展示文本只用于 UI 与诊断；运行时对象引用仅在 Mod 内部用于再次定位订单，不会序列化给前端。
/// <c>ManualOrder</c> 表示最近一次读取的瞬时属性，手动评价绑定字段则保存精确 setter 在该活动订单生命周期内建立的不可变证据。
/// </remarks>
public sealed record CapturedRuntimeSpecialOrder(
    int DeskCode,
    int? GuestId,
    string GuestName,
    int FoodTagId,
    bool HasFoodTagId,
    string FoodTagDisplayText,
    int BeverageTagId,
    bool HasBeverageTagId,
    string BeverageTagDisplayText,
    bool IsFreeOrder,
    bool IsFulfilled,
    DateTime FirstCapturedAt,
    DateTime CapturedAt,
    string RuntimeKey,
    string CaptureSource)
{
    internal object? OrderObject { get; init; }
    internal object? ControllerObject { get; init; }
    public bool ManualOrder { get; init; }
    internal object? ManualEvaluationCallback { get; init; }
    public bool ManualEvaluationBindingObserved { get; init; }
    public bool ManualEvaluationBindingConflict { get; init; }
    internal object? ManualEvaluationBindingCallback { get; init; }
}

/// <summary>
/// 稀客订单 Hook 解析失败的简短诊断记录。
/// </summary>
internal sealed record RuntimeParseFailureDiagnostic(
    DateTime CapturedAtUtc,
    string Message);
