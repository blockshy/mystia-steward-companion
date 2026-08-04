using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 捕获普通客人订单与其 <c>GuestGroupController</c> 的运行时绑定关系。
/// </summary>
/// <remarks>
/// HUD 的 <c>OrderController</c> 只能说明订单仍对玩家可见，不能保证订单仍有可执行的客人控制器。
/// 普客自动化送达、恢复耐心和评价都必须通过 <c>GuestGroupController</c>，因此这里在
/// <c>GuestGroupController.PushToOrder</c> 和 <c>GuestsManager.SetManualControllerOrderInternal</c>
/// 阶段记录真实归属，并在订单移除或评价后清理。
/// </remarks>
public static class NormalOrderRuntimeCapture
{
    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string OrderBaseTypeName = "NightScene.GuestManagementUtility.GuestsManager+OrderBase";
    private const string EvaluationResultTypeName =
        "NightScene.GuestManagementUtility.GuestGroupController+EvaluationResult";
    private const string LeaveTypeName =
        "NightScene.GuestManagementUtility.GuestGroupController+LeaveType";
    private const string Il2CppActionGenericTypeName = "Il2CppSystem.Action`1";
    private const string Il2CppActionTypeName = "Il2CppSystem.Action";
    private const int MaxOrders = 64;

    private static readonly object SyncRoot = new();
    private static readonly List<CapturedRuntimeNormalOrder> Orders = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly string[] RequiredPatchKeys =
    {
        PatchKey(GuestGroupControllerTypeName, "PushToOrder", 1, false),
        PatchKey(GuestsManagerTypeName, "SetManualControllerOrderInternal", 3, false),
        PatchKey(GuestsManagerTypeName, "RemoveFromOrder", 1, false),
        PatchKey(GuestsManagerTypeName, "EvaluateOrder", 3, false),
        PatchKey(GuestsManagerTypeName, "EvaulateManualOrder", 2, false),
        PatchKey(GuestsManagerTypeName, "CleanOrderInfo", 1, false),
        PatchKey(GuestsManagerTypeName, "RepellInternal", 4, false),
    };
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static DateTime _lastAttachAttemptUtc = DateTime.MinValue;
    private static string _status = "not attached";
    private static long _firstCoveredBusinessGeneration = long.MaxValue;
    private static long _changeVersion;
    private static int _addCallbacks;
    private static int _removeCallbacks;
    private static int _capturedOrders;
    private static int _parseFailures;
    private static string _lastCapture = "";
    private static string _lastParseFailure = "";

    /// <summary>
    /// 捕获记录变更版本号，供主线程刷新快照时做轻量变更检测。
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
    /// 返回当前 Hook 安装和最近捕获状态，用于快照来源诊断。
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
    /// 尝试安装普通订单生命周期 Hook。
    /// </summary>
    /// <param name="log">BepInEx 日志源，用于记录安装成功或等待游戏类型加载。</param>
    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(log, true);
    }

    /// <summary>
    /// 重置延迟重试时间，让下一次快照读取可以立刻重新尝试安装 Hook。
    /// </summary>
    public static void ResetAttachRetryDelay()
    {
        lock (SyncRoot)
        {
            _lastAttachAttemptUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 返回最近捕获且仍在保留窗口内的普客订单绑定快照。
    /// </summary>
    /// <param name="maxAge">最后一次捕获后允许保留的最长时间。</param>
    /// <returns>按首次捕获时间排序的捕获记录副本。</returns>
    public static IReadOnlyList<CapturedRuntimeNormalOrder> Snapshot(TimeSpan maxAge)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return Array.Empty<CapturedRuntimeNormalOrder>();

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
    /// 清空当前缓存的普客订单绑定。
    /// </summary>
    /// <param name="reason">清理原因，用于调试状态显示。</param>
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
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.normal-order-runtime-capture");

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
                "RemoveFromOrder",
                1,
                false,
                nameof(CaptureOrderBeforeRemoval),
                nameof(OnOrderRemovalSucceeded),
                patchedNow,
                missing,
                exactMethodPredicate: IsExactOrderBaseMethod);
            PatchMethod(
                _harmony,
                GuestsManagerTypeName,
                "EvaluateOrder",
                3,
                false,
                nameof(CaptureControllerOrderBeforeCompletion),
                nameof(OnControllerOrderCompletionSucceeded),
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
                nameof(OnControllerOrderCompletionSucceeded),
                patchedNow,
                missing,
                exactMethodPredicate: IsExactManualEvaluateOrder);
            PatchMethod(
                _harmony,
                GuestsManagerTypeName,
                "CleanOrderInfo",
                1,
                false,
                nameof(CaptureControllerOrderBeforeCleanup),
                nameof(OnControllerOrderCleanupSucceeded),
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
                nameof(OnControllerOrderRepellSucceeded),
                patchedNow,
                missing,
                exactMethodPredicate: IsExactRepellInternal);

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
                log?.LogInfo($"Normal order runtime capture patched: {string.Join(", ", patchedNow)}.");
            }
            else if (force && !IsBusinessReady)
            {
                log?.LogWarning($"Normal order runtime capture waiting for game types: {string.Join(", ", missing.Take(4))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _status = $"error: {ex.Message}";
            }

            log?.LogWarning($"Normal order runtime capture failed: {ex.Message}");
        }
    }

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

        var type = RuntimeReflectionUtility.FindType(typeName);
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
        var target = requireExactManualOrderSetter || exactMethodPredicate != null
            ? candidates.Length == 1 ? candidates[0] : null
            : candidates.FirstOrDefault();
        var prefix = prefixName == null ? null : typeof(NormalOrderRuntimeCapture).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
        var postfix = postfixName == null ? null : typeof(NormalOrderRuntimeCapture).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
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
                OrderBaseTypeName,
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
        return HasExactVoidSignature(method, OrderBaseTypeName);
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
            var lifecycleSequence = BeginOrderLifecycle(
                __0,
                __instance,
                "ControllerOrderAdd");
            var order = ParseOrder(__0, "ControllerOrderAdd", __instance);
            AddOrder(order == null || lifecycleSequence <= 0
                ? null
                : order with { OrderLifecycleSequence = lifecycleSequence });
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
            lock (SyncRoot) _addCallbacks++;
            var lifecycleSequence = BeginOrderLifecycle(__2, __0, "ManualOrderSet");
            var order = ParseOrder(__2, "ManualOrderSet", __0);
            if (order is not { ManualOrder: true })
            {
                NoteParseFailure("ManualOrderSet", "OrderBase.ManualOrder was not true after the native setter");
                return;
            }

            if (__1 == null)
            {
                NoteParseFailure("ManualOrderSet", "manual evaluation callback is null");
            }

            AddOrder(lifecycleSequence <= 0
                ? null
                : order with
                {
                    OrderLifecycleSequence = lifecycleSequence,
                    ManualEvaluationCallback = __1,
                    ManualEvaluationBindingObserved = true,
                    ManualEvaluationBindingCallback = __1,
                });
        });
    }

    private static void CaptureOrderBeforeRemoval(object __0, out TerminalOrderCaptureState? __state)
    {
        TerminalOrderCaptureState? state = null;
        RunCaptureCallback("OrderRemove.Before", () =>
        {
            state = CaptureOrderRemovalState(__0, "OrderRemove.Before");
        });
        __state = state;
    }

    private static void OnOrderRemovalSucceeded(TerminalOrderCaptureState? __state, bool __runOriginal)
    {
        if (!__runOriginal || __state == null) return;

        RunTerminalPostfix("OrderRemove.After", () =>
        {
            lock (SyncRoot) _removeCallbacks++;
            PublishAndRemoveOrder(
                __state,
                RuntimeOrderTerminalDisposition.Removed,
                RuntimeOrderTerminalReceiptSource.RemoveFromOrder);
        });
    }

    private static void CaptureControllerOrderBeforeCompletion(
        object __0,
        MethodBase __originalMethod,
        out TerminalOrderCaptureState? __state)
    {
        TerminalOrderCaptureState? state = null;
        var source = $"{__originalMethod.Name}.Before";
        RunCaptureCallback(source, () =>
        {
            state = CaptureControllerTerminalState(__0, source);
        });
        __state = state;
    }

    private static void OnControllerOrderCompletionSucceeded(
        TerminalOrderCaptureState? __state,
        MethodBase __originalMethod,
        bool __runOriginal)
    {
        if (!__runOriginal || __state is not { IsFulfilled: true }) return;

        RunTerminalPostfix($"{__originalMethod.Name}.After", () =>
        {
            lock (SyncRoot) _removeCallbacks++;
            var receiptSource = __originalMethod.Name switch
            {
                "EvaluateOrder" => RuntimeOrderTerminalReceiptSource.EvaluateOrder,
                "EvaulateManualOrder" => RuntimeOrderTerminalReceiptSource.EvaulateManualOrder,
                _ => throw new InvalidOperationException(
                    $"Unexpected evaluation Hook source {__originalMethod.Name}."),
            };
            PublishAndRemoveOrder(
                __state,
                RuntimeOrderTerminalDisposition.Evaluated,
                receiptSource);
        });
    }

    private static void CaptureControllerOrderBeforeCleanup(
        object __0,
        MethodBase __originalMethod,
        out TerminalOrderCaptureState? __state)
    {
        TerminalOrderCaptureState? state = null;
        var source = $"{__originalMethod.Name}.Before";
        RunCaptureCallback(source, () =>
        {
            state = CaptureControllerTerminalState(__0, source);
        });
        __state = state;
    }

    private static void OnControllerOrderCleanupSucceeded(
        TerminalOrderCaptureState? __state,
        MethodBase __originalMethod,
        bool __runOriginal)
    {
        if (!__runOriginal || __state == null) return;

        RunTerminalPostfix($"{__originalMethod.Name}.After", () =>
        {
            lock (SyncRoot) _removeCallbacks++;
            if (!string.Equals(__originalMethod.Name, "CleanOrderInfo", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected cleanup Hook source {__originalMethod.Name}.");
            }
            PublishAndRemoveOrder(
                __state,
                RuntimeOrderTerminalDisposition.Removed,
                RuntimeOrderTerminalReceiptSource.CleanOrderInfo);
        });
    }

    private static void CaptureControllerOrderBeforeRepell(
        object __0,
        MethodBase __originalMethod,
        out TerminalOrderCaptureState? __state)
    {
        CaptureControllerOrderBeforeCleanup(__0, __originalMethod, out __state);
    }

    private static void OnControllerOrderRepellSucceeded(
        TerminalOrderCaptureState? __state,
        MethodBase __originalMethod,
        bool __runOriginal)
    {
        if (!__runOriginal || __state == null) return;

        RunTerminalPostfix($"{__originalMethod.Name}.After", () =>
        {
            lock (SyncRoot) _removeCallbacks++;
            if (!string.Equals(__originalMethod.Name, "RepellInternal", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unexpected repell Hook source {__originalMethod.Name}.");
            }
            PublishAndRemoveOrder(
                __state,
                RuntimeOrderTerminalDisposition.Removed,
                RuntimeOrderTerminalReceiptSource.RepellInternal);
        });
    }

    private static void PublishAndRemoveOrder(
        TerminalOrderCaptureState state,
        RuntimeOrderTerminalDisposition disposition,
        RuntimeOrderTerminalReceiptSource receiptSource)
    {
        var removeCapturedLifecycle = RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(
            state.ToBindingToken());
        var binding = state.ToBindingToken();
        try
        {
            RuntimeOrderTerminalReceiptStore.Publish(new RuntimeOrderTerminalHookState(
                binding.BusinessGeneration,
                binding.OrderKind,
                binding.OrderPointer,
                binding.ControllerPointer,
                binding.LifecycleSequence,
                disposition,
                receiptSource));
        }
        finally
        {
            if (removeCapturedLifecycle)
            {
                RemoveOrder(state.ToBindingToken());
            }
        }
    }

    private static long BeginOrderLifecycle(
        object order,
        object controller,
        string source)
    {
        var lifecycleBefore = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycleBefore.IsActive || lifecycleBefore.Generation <= 0) return 0;

        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (resolution.Resolved && resolution.Kind != RuntimeOrderKind.Normal) return 0;
        if (!resolution.Resolved || resolution.ReadableOrder == null)
        {
            NoteParseFailure(source, $"order lifecycle start concrete type is unavailable: {resolution.Reason}");
            return 0;
        }

        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(resolution.ReadableOrder, out var orderPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(controller, out var controllerPointer))
        {
            NoteParseFailure(source, "order lifecycle start lacks exact native order/controller identity");
            return 0;
        }

        if (!TryReadExactOrderBool(controller, "HasEvaluated", out var hasEvaluated)
            || hasEvaluated)
        {
            NoteParseFailure(source, "order lifecycle start requires exact HasEvaluated=false after native return");
            return 0;
        }

        var currentOrder = RuntimeReflectionUtility.InvokeMethod(controller, "PeekOrders");
        var currentResolution = RuntimeOrderTypeResolver.Resolve(currentOrder);
        if (!currentResolution.Resolved
            || currentResolution.Kind != RuntimeOrderKind.Normal
            || currentResolution.ReadableOrder == null
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(currentResolution.ReadableOrder, out var currentOrderPointer)
            || currentOrderPointer != orderPointer)
        {
            NoteParseFailure(source, "order lifecycle start no longer owns the exact PeekOrders stack top");
            return 0;
        }

        var sequence = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
            lifecycleBefore.Generation,
            RuntimeOrderKind.Normal,
            orderPointer,
            controllerPointer);
        var lifecycleAfter = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycleAfter.IsActive || lifecycleAfter.Generation != lifecycleBefore.Generation)
        {
            NoteParseFailure(source, "night-business generation changed during order lifecycle start");
            return 0;
        }

        return sequence;
    }

    private static TerminalOrderCaptureState? CaptureControllerTerminalState(
        object controller,
        string source)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive || lifecycle.Generation <= 0) return null;

        if (IsEvaluationTerminalSource(source))
        {
            if (!TryReadExactOrderBool(controller, "HasEvaluated", out var hasEvaluated))
            {
                NoteParseFailure(source, "exact GuestGroupController.HasEvaluated bool property is unavailable");
                return null;
            }

            if (hasEvaluated) return null;
        }

        var order = RuntimeReflectionUtility.InvokeMethod(controller, "PeekOrders");
        return CreateTerminalCaptureState(
            order,
            controller,
            lifecycle.Generation,
            source,
            requireFulfilled: IsEvaluationTerminalSource(source));
    }

    private static bool IsEvaluationTerminalSource(string source)
    {
        return source.StartsWith("EvaluateOrder.", StringComparison.Ordinal)
            || source.StartsWith("EvaulateManualOrder.", StringComparison.Ordinal);
    }

    private static TerminalOrderCaptureState? CaptureOrderRemovalState(object order, string source)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive || lifecycle.Generation <= 0) return null;
        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (resolution.Resolved && resolution.Kind != RuntimeOrderKind.Normal) return null;
        if (!resolution.Resolved
            || resolution.ReadableOrder == null
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(resolution.ReadableOrder, out var orderPointer))
        {
            NoteParseFailure(source, "RemoveFromOrder did not provide one exact concrete NormalOrder");
            return null;
        }

        if (!RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycleByOrder(
                lifecycle.Generation,
                RuntimeOrderKind.Normal,
                orderPointer,
                out var controllerPointer,
                out var lifecycleSequence))
        {
            NoteParseFailure(
                source,
                $"RemoveFromOrder has no unique active order lifecycle: pointer=0x{(long)orderPointer:X}");
            return null;
        }

        var currentLifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!currentLifecycle.IsActive || currentLifecycle.Generation != lifecycle.Generation)
        {
            NoteParseFailure(
                source,
                $"night-business generation changed during terminal capture: expected={lifecycle.Generation}, current={currentLifecycle.Generation}, active={currentLifecycle.IsActive}");
            return null;
        }

        return new TerminalOrderCaptureState(
            new RuntimeOrderBindingToken(
                lifecycle.Generation,
                RuntimeOrderKind.Normal,
                orderPointer,
                controllerPointer,
                lifecycleSequence),
            IsFulfilled: false);
    }

    private static TerminalOrderCaptureState? CreateTerminalCaptureState(
        object? order,
        object controller,
        long businessGeneration,
        string source,
        bool requireFulfilled)
    {
        if (order == null) return null;

        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (resolution.Resolved && resolution.Kind != RuntimeOrderKind.Normal) return null;
        if (!resolution.Resolved || resolution.ReadableOrder == null)
        {
            NoteParseFailure(source, "terminal Hook did not resolve one concrete NormalOrder");
            return null;
        }

        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(resolution.ReadableOrder, out var orderPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(controller, out var controllerPointer))
        {
            NoteParseFailure(source, "terminal Hook native order/controller identity is incomplete");
            return null;
        }

        var currentLifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!currentLifecycle.IsActive || currentLifecycle.Generation != businessGeneration)
        {
            NoteParseFailure(
                source,
                $"night-business generation changed during terminal capture: expected={businessGeneration}, current={currentLifecycle.Generation}, active={currentLifecycle.IsActive}");
            return null;
        }

        if (!RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycle(
                businessGeneration,
                resolution.Kind,
                orderPointer,
                controllerPointer,
                out var lifecycleSequence))
        {
            NoteParseFailure(source, "terminal Hook has no exact active order-lifecycle sequence");
            return null;
        }

        var isFulfilled = false;
        if (requireFulfilled
            && !TryReadExactOrderBool(resolution.ReadableOrder, "IsFullfilled", out isFulfilled))
        {
            NoteParseFailure(source, "exact OrderBase.IsFullfilled bool property is unavailable");
            return null;
        }

        return new TerminalOrderCaptureState(
            new RuntimeOrderBindingToken(
                businessGeneration,
                resolution.Kind,
                orderPointer,
                controllerPointer,
                lifecycleSequence),
            isFulfilled);
    }

    private static void RunCaptureCallback(string source, Action callback)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return;

        RunTerminalPostfix(source, callback);
    }

    private static void RunTerminalPostfix(string source, Action callback)
    {

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            NoteParseFailure(source, $"capture callback failed: {ex.GetBaseException().Message}");
        }
    }

    private static CapturedRuntimeNormalOrder? ParseOrder(object? order, string source, object? controller = null)
    {
        if (order == null)
        {
            NoteParseFailure(source, "order is null");
            return null;
        }

        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (!resolution.RecognizedOrderType)
        {
            return null;
        }

        if (!resolution.Resolved)
        {
            NoteParseFailure(source, resolution.Reason);
            return null;
        }

        if (resolution.Kind != RuntimeOrderKind.Normal || resolution.ReadableOrder == null)
        {
            return null;
        }

        var readableOrder = resolution.ReadableOrder;
        if (!TryReadExactManualOrder(readableOrder, out var manualOrder))
        {
            NoteParseFailure(source, "exact OrderBase.ManualOrder bool property is unavailable");
            return null;
        }
        if (!TryReadExactOrderBool(readableOrder, "IsFullfilled", out var isFulfilled))
        {
            NoteParseFailure(source, "exact OrderBase.IsFullfilled bool property is unavailable");
            return null;
        }

        var requestFood = RuntimeReflectionUtility.GetMemberValue(readableOrder, "RequestFood")
            ?? RuntimeReflectionUtility.InvokeMethod(readableOrder, "get_RequestFood");
        var requestBeverage = RuntimeReflectionUtility.GetMemberValue(readableOrder, "RequestBeverage")
            ?? RuntimeReflectionUtility.InvokeMethod(readableOrder, "get_RequestBeverage");
        var foodId = ReadSellableId(requestFood, ReadFirstMember(readableOrder, "foodRequest", "FoodRequest", "requestFoodId", "RequestFoodId", "RequestFoodID"));
        var beverageId = ReadSellableId(requestBeverage, ReadFirstMember(readableOrder, "beverageRequest", "BeverageRequest", "requestBevId", "RequestBevId", "requestBeverageId", "RequestBeverageId", "RequestBeverageID"));
        var deskCode = RuntimeReflectionUtility.ToInt(
            RuntimeReflectionUtility.GetMemberValue(readableOrder, "DeskCode")
            ?? RuntimeReflectionUtility.GetMemberValue(controller, "DeskCode"),
            -1);
        if (deskCode < 0 || (foodId < 0 && beverageId < 0))
        {
            NoteParseFailure(source, $"missing key fields: desk={deskCode}, food={foodId}, beverage={beverageId}");
            return null;
        }

        var guest = RuntimeReflectionUtility.GetMemberValue(readableOrder, "Guest")
            ?? RuntimeReflectionUtility.InvokeMethod(readableOrder, "get_Guest")
            ?? RuntimeReflectionUtility.GetMemberValue(controller, "OrderingGuest");
        var guestName = ReadTextLikeValue(guest);
        var capturedAt = DateTime.UtcNow;
        return new CapturedRuntimeNormalOrder(
            RuntimeOrderKey(readableOrder),
            deskCode,
            string.IsNullOrWhiteSpace(guestName) ? "普客" : guestName,
            foodId,
            beverageId,
            capturedAt,
            capturedAt,
            source)
        {
            OrderObject = readableOrder,
            ControllerObject = controller,
            ManualOrder = manualOrder,
            IsFulfilled = isFulfilled,
        };
    }

    private static void AddOrder(CapturedRuntimeNormalOrder? order)
    {
        if (order == null) return;
        if (order.OrderObject == null
            || order.ControllerObject == null
            || string.IsNullOrWhiteSpace(order.RuntimeKey)
            || order.OrderLifecycleSequence <= 0)
        {
            NoteParseFailure(order.CaptureSource, "native order/controller binding is incomplete");
            return;
        }

        lock (SyncRoot)
        {
            var existing = Orders.Where(current => IsSameOrderSlot(current, order)).ToList();
            Orders.RemoveAll(current => IsSameNativeOrderSlot(current, order));
            var next = existing.Aggregate(order, MergeCapturedOrder);
            Orders.Add(next);
            _capturedOrders++;
            _lastCapture = $"{next.CaptureSource}: desk={next.DeskCode + 1}, food={next.FoodId}, beverage={next.BeverageId}, manual={next.ManualOrder}, manualCallback={(next.ManualEvaluationCallback == null ? "no" : "yes")}, manualBinding={DescribeManualEvaluationBinding(next.ManualEvaluationBindingObserved, next.ManualEvaluationBindingConflict, next.ManualEvaluationBindingCallback)}, obj={(next.OrderObject == null ? "no" : "yes")}/{(next.ControllerObject == null ? "no" : "yes")}";
            _changeVersion++;
            if (Orders.Count > MaxOrders)
            {
                Orders.RemoveRange(0, Orders.Count - MaxOrders);
            }

            _status = BuildStatusLocked();
        }
    }

    private static void RemoveOrder(RuntimeOrderBindingToken binding)
    {
        var runtimeKey = $"ptr:{(long)binding.OrderPointer:x}";
        lock (SyncRoot)
        {
            var removed = Orders.RemoveAll(existing =>
                existing.OrderLifecycleSequence == binding.LifecycleSequence
                && string.Equals(existing.RuntimeKey, runtimeKey, StringComparison.Ordinal));
            _lastCapture = $"removed: order={runtimeKey}, lifecycle={binding.LifecycleSequence}";
            if (removed > 0) _changeVersion++;
            _status = BuildStatusLocked();
        }
    }

    private static CapturedRuntimeNormalOrder MergeCapturedOrder(
        CapturedRuntimeNormalOrder incoming,
        CapturedRuntimeNormalOrder existing)
    {
        if (!CanMergeCapturedOrderDetails(incoming, existing))
        {
            return incoming;
        }

        var manualBindingConflict = incoming.ManualEvaluationBindingConflict
            || existing.ManualEvaluationBindingConflict
            || HaveConflictingManualEvaluationBindings(incoming, existing);
        var manualBindingCallback = incoming.ManualEvaluationBindingObserved
            ? incoming.ManualEvaluationBindingCallback ?? existing.ManualEvaluationBindingCallback
            : existing.ManualEvaluationBindingCallback;

        return incoming with
        {
            GuestName = string.IsNullOrWhiteSpace(incoming.GuestName) || string.Equals(incoming.GuestName, "普客", StringComparison.Ordinal)
                ? existing.GuestName
                : incoming.GuestName,
            FirstCapturedAt = existing.FirstCapturedAt < incoming.FirstCapturedAt ? existing.FirstCapturedAt : incoming.FirstCapturedAt,
            RuntimeKey = string.IsNullOrWhiteSpace(incoming.RuntimeKey) ? existing.RuntimeKey : incoming.RuntimeKey,
            CaptureSource = MergeCaptureSource(existing.CaptureSource, incoming.CaptureSource),
            OrderObject = incoming.OrderObject ?? existing.OrderObject,
            ControllerObject = incoming.ControllerObject ?? existing.ControllerObject,
            ManualOrder = incoming.ManualOrder,
            IsFulfilled = incoming.IsFulfilled || existing.IsFulfilled,
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

    private static bool CanMergeCapturedOrderDetails(
        CapturedRuntimeNormalOrder incoming,
        CapturedRuntimeNormalOrder existing)
    {
        return incoming.DeskCode == existing.DeskCode
            && incoming.FoodId == existing.FoodId
            && incoming.BeverageId == existing.BeverageId;
    }

    private static bool HaveConflictingManualEvaluationBindings(
        CapturedRuntimeNormalOrder incoming,
        CapturedRuntimeNormalOrder existing)
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

    private static bool IsSameOrderSlot(CapturedRuntimeNormalOrder left, CapturedRuntimeNormalOrder right)
    {
        return IsSameNativeOrderSlot(left, right)
            && left.OrderLifecycleSequence > 0
            && left.OrderLifecycleSequence == right.OrderLifecycleSequence;
    }

    private static bool IsSameNativeOrderSlot(
        CapturedRuntimeNormalOrder left,
        CapturedRuntimeNormalOrder right)
    {
        return !string.IsNullOrWhiteSpace(left.RuntimeKey)
            && !string.IsNullOrWhiteSpace(right.RuntimeKey)
            && string.Equals(left.RuntimeKey, right.RuntimeKey, StringComparison.Ordinal);
    }

    private static object? ReadFirstMember(object? instance, params string[] names)
    {
        foreach (var name in names)
        {
            var value = RuntimeReflectionUtility.GetMemberValue(instance, name);
            if (value != null) return value;
        }

        return null;
    }

    private static int ReadSellableId(object? sellable, object? fallback)
    {
        var value = RuntimeReflectionUtility.InvokeMethod(sellable, "get_id")
            ?? RuntimeReflectionUtility.InvokeMethod(sellable, "get_Id")
            ?? RuntimeReflectionUtility.GetMemberValue(sellable, "id")
            ?? RuntimeReflectionUtility.GetMemberValue(sellable, "Id")
            ?? fallback;
        return RuntimeReflectionUtility.ToInt(value, -1);
    }

    private static string ReadTextLikeValue(object? value)
    {
        if (value == null) return "";
        foreach (var name in new[] { "Name", "name", "LocalizedName", "Text", "text", "StringId", "stringId" })
        {
            var member = RuntimeReflectionUtility.GetMemberValue(value, name);
            if (member is string text && IsReadableText(text)) return text.Trim();
        }

        var fallback = value.ToString();
        return IsReadableText(fallback) ? fallback!.Trim() : "";
    }

    private static bool IsReadableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Contains("GameData.", StringComparison.Ordinal)) return false;
        if (value.Contains("NightScene.", StringComparison.Ordinal)) return false;
        if (value.Contains("Il2Cpp", StringComparison.Ordinal)) return false;
        return true;
    }

    private static string RuntimeOrderKey(object order)
    {
        return RuntimeReflectionUtility.TryReadNativeObjectPointer(order, out var pointer)
            ? $"ptr:{pointer:x}"
            : "";
    }

    private static string MergeCaptureSource(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing)) return incoming;
        if (string.IsNullOrWhiteSpace(incoming)) return existing;

        var parts = existing
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(incoming.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return string.Join("+", parts);
    }

    private static void NoteParseFailure(string source, string reason)
    {
        lock (SyncRoot)
        {
            _parseFailures++;
            _lastParseFailure = $"{source}: {reason}";
            _status = BuildStatusLocked();
        }
    }

    private static string BuildStatusLocked()
    {
        var missing = RequiredPatchKeys.Where(key => !PatchedMethods.Contains(key)).Select(ShortPatchKey).ToArray();
        var covered = missing.Length == 0
            && (!RuntimeNightBusinessLifecycle.IsActive
                || RuntimeNightBusinessLifecycle.Generation >= _firstCoveredBusinessGeneration);
        return $"ready={(covered ? "yes" : "no")}; required={RequiredPatchKeys.Length - missing.Length}/{RequiredPatchKeys.Length}; firstCoveredGeneration={(_firstCoveredBusinessGeneration == long.MaxValue ? "pending" : _firstCoveredBusinessGeneration)}; currentGeneration={RuntimeNightBusinessLifecycle.Generation}; missing=[{string.Join(",", missing)}]; patched={PatchedMethods.Count}; active={Orders.Count}; captured={_capturedOrders}; add={_addCallbacks}; remove={_removeCallbacks}; parseFailures={_parseFailures}; last={RuntimeReflectionUtility.Trim(_lastCapture, 120)}; lastFailure={RuntimeReflectionUtility.Trim(_lastParseFailure, 120)}";
    }

    private sealed record TerminalOrderCaptureState(
        RuntimeOrderBindingToken Binding,
        bool IsFulfilled)
    {
        public RuntimeOrderBindingToken ToBindingToken() => Binding;
    }

    private static string ShortPatchKey(string key)
    {
        var separator = key.LastIndexOf('.');
        return separator >= 0 ? key[(separator + 1)..] : key;
    }
}

/// <summary>
/// 一条从游戏运行时捕获到的普通客人订单绑定。
/// </summary>
/// <remarks>
/// 运行时对象引用只在 Mod 内部用于重新定位可执行订单，不会序列化给前端。
/// <c>ManualOrder</c> 表示最近一次读取的瞬时属性，手动评价绑定字段则保存精确 setter 在该活动订单生命周期内建立的不可变证据。
/// </remarks>
public sealed record CapturedRuntimeNormalOrder(
    string RuntimeKey,
    int DeskCode,
    string GuestName,
    int FoodId,
    int BeverageId,
    DateTime FirstCapturedAt,
    DateTime CapturedAt,
    string CaptureSource)
{
    internal object? OrderObject { get; init; }
    internal object? ControllerObject { get; init; }
    internal long OrderLifecycleSequence { get; init; }
    public bool ManualOrder { get; init; }
    public bool IsFulfilled { get; init; }
    internal object? ManualEvaluationCallback { get; init; }
    public bool ManualEvaluationBindingObserved { get; init; }
    public bool ManualEvaluationBindingConflict { get; init; }
    internal object? ManualEvaluationBindingCallback { get; init; }
}
