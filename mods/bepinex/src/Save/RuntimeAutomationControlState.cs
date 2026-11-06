using System.Text.Json;

namespace MystiaStewardCompanion.Save;

internal enum RuntimeAutomationControlTargetKind
{
    Rare,
    Normal,
}

internal enum RuntimeAutomationControlStage
{
    FoodDelivery,
    OrderEvaluation,
    YuumaSettlement,
}

internal readonly record struct RuntimeAutomationControlDecision(
    bool Allowed,
    string State,
    string ReasonCode,
    string Message,
    long AuthorityRevision,
    bool DeliveryConfigured,
    bool CompletionConfigured);

/// <summary>
/// 从当前已确认的共享配置提取的调度策略。只保存标量，不持有 JSON 或游戏对象；
/// 队列恢复同时服务手动高亮，不能把自动化执行租约或料理开关当作此策略的启用条件。
/// </summary>
internal readonly record struct RuntimeRareGuestAutoResumeSettings(
    long AuthorityRevision, bool Enabled, int ThresholdPercent, bool ToFront);

/// <summary>
/// Holds the control-state lock across one native side-effect boundary.
/// </summary>
/// <remarks>
/// Authority/profile changes wait for an already admitted boundary to finish. A boundary that has
/// not started observes the new state and remains suspended instead. The permit must be disposed on
/// the thread that acquired it.
/// </remarks>
internal sealed class RuntimeAutomationControlPermit : IDisposable
{
    private Action? _release;

    internal RuntimeAutomationControlPermit(
        RuntimeAutomationControlDecision decision,
        Action? release)
    {
        Decision = decision;
        _release = release;
    }

    public RuntimeAutomationControlDecision Decision { get; }
    public bool Allowed => Decision.Allowed;

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// Process-local authority for future automation side effects of already-started cooking jobs.
/// </summary>
/// <remarks>
/// The profile comes only from the currently authoritative companion-device record. A valid lease
/// for that exact authority revision is additionally required. No Unity or IL2CPP object is stored
/// here; cooking-job identity remains owned by <c>RuntimeOrderPreparationService</c> and is retired at
/// the night-business boundary.
/// </remarks>
internal static class RuntimeAutomationControlState
{
    private static readonly object SyncRoot = new();
    private static RuntimeAutomationControlProfile? _profile;
    // 有效名单与模块状态一并发布；关闭模块保留配置，仅清空执行所用的名单。
    private static int[] _configuredManagedRareGuestIds = Array.Empty<int>();
    private static bool _participationModuleEnabled;
    private static RuntimeRareGuestAutoResumeSettings _rareAutoResume = new(0, false, 30, false);
    private static long _authorityRevision;
    private static long _leaseAuthorityRevision;
    private static DateTime _leaseExpiresAtUtc;
    private static string _leaseBlockReasonCode = "automation-authority-unavailable";
    private static string _leaseBlockMessage = "主设备连接与自动化配置尚未就绪。";
    private static Func<bool>? _resourceReady;

    /// <summary>
    /// 持续料理的送达/评价同样受图片初始化门禁约束。只持有纯托管只读函数，不保存 Unity 对象；
    /// 调用方必须保证函数不反向取得本类锁，资源服务也不得在自身锁内发布状态事件。
    /// null 仅供没有资源服务的隔离宿主沿用既有行为，生产始终传入真实服务。
    /// </summary>
    internal static void ConfigureResourceReadiness(Func<bool>? resourceReady)
    {
        lock (SyncRoot) _resourceReady = resourceReady;
    }

    public static void PublishAuthority(
        JsonElement activeProfile,
        long authorityRevision,
        string reasonCode,
        string message)
    {
        if (authorityRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(authorityRevision));
        }

        var profile = RuntimeAutomationControlProfile.Parse(activeProfile);
        var enabled = activeProfile.TryGetProperty("rareGuestParticipationModuleEnabled", out var module) && module.ValueKind == JsonValueKind.True;
        var managed = ParseManagedGuestIds(activeProfile);
        var autoResume = ParseRareAutoResume(activeProfile, authorityRevision, enabled);
        lock (SyncRoot)
        {
            var changed = _authorityRevision != authorityRevision || _profile != profile || _rareAutoResume != autoResume;
            _profile = profile;
            _participationModuleEnabled = enabled;
            _configuredManagedRareGuestIds = managed;
            _rareAutoResume = autoResume;
            _authorityRevision = authorityRevision;
            if (!changed) return;

            _leaseAuthorityRevision = 0;
            _leaseExpiresAtUtc = DateTime.MinValue;
            _leaseBlockReasonCode = RequireText(reasonCode, nameof(reasonCode));
            _leaseBlockMessage = RequireText(message, nameof(message));
        }
    }

    public static void PublishLease(long authorityRevision, DateTime expiresAtUtc)
    {
        lock (SyncRoot)
        {
            if (_profile == null || authorityRevision <= 0 || authorityRevision != _authorityRevision)
            {
                throw new InvalidOperationException(
                    "Automation lease does not match the published companion authority revision.");
            }

            _leaseAuthorityRevision = authorityRevision;
            _leaseExpiresAtUtc = expiresAtUtc.ToUniversalTime();
            _leaseBlockReasonCode = "";
            _leaseBlockMessage = "";
        }
    }

    public static void RevokeLease(string reasonCode, string message)
    {
        lock (SyncRoot)
        {
            _leaseAuthorityRevision = 0;
            _leaseExpiresAtUtc = DateTime.MinValue;
            _leaseBlockReasonCode = RequireText(reasonCode, nameof(reasonCode));
            _leaseBlockMessage = RequireText(message, nameof(message));
        }
    }

    public static void Reset(string message)
    {
        lock (SyncRoot)
        {
            _profile = null;
            _participationModuleEnabled = false;
            _rareAutoResume = new(0, false, 30, false);
            _configuredManagedRareGuestIds = Array.Empty<int>();
            _authorityRevision = 0;
            _leaseAuthorityRevision = 0;
            _leaseExpiresAtUtc = DateTime.MinValue;
            _leaseBlockReasonCode = "automation-authority-unavailable";
            _leaseBlockMessage = RequireText(message, nameof(message));
        }
    }

    /// <summary>只复制当前生效名单；未启用模块与空名单完全沿用原自动化流程。</summary>
    public static IReadOnlyList<int> SnapshotManagedRareGuestIds()
    {
        lock (SyncRoot) return _participationModuleEnabled
            ? _configuredManagedRareGuestIds.ToArray() : Array.Empty<int>();
    }

    public static bool SnapshotRareGuestParticipationModuleEnabled()
    {
        lock (SyncRoot) return _participationModuleEnabled;
    }

    /// <summary>发布节奏内读取策略；模块关闭时保留配置，却不允许自动恢复任何暂停订单。</summary>
    internal static RuntimeRareGuestAutoResumeSettings SnapshotRareGuestAutoResumeSettings()
    {
        lock (SyncRoot) return _rareAutoResume;
    }

    /// <summary>
    /// 共享存储负责 schema 迁移；运行时仍拒绝畸形值。缺失字段只供旧内部夹具使用关闭默认，
    /// 不得把非法位置或越界阈值静默修正为一个会改变队列的有效策略。
    /// </summary>
    private static RuntimeRareGuestAutoResumeSettings ParseRareAutoResume(JsonElement profile, long revision, bool moduleEnabled)
    {
        var fieldCount = (profile.TryGetProperty("rareGuestParticipationAutoResumeEnabled", out _) ? 1 : 0)
            + (profile.TryGetProperty("rareGuestParticipationAutoResumeThresholdPercent", out _) ? 1 : 0)
            + (profile.TryGetProperty("rareGuestParticipationAutoResumePosition", out _) ? 1 : 0);
        if (fieldCount is > 0 and < 3)
            throw new InvalidDataException("等待条自动启用配置必须同时提供开关、阈值和队列位置。");
        var enabled = false;
        if (profile.TryGetProperty("rareGuestParticipationAutoResumeEnabled", out var enabledValue))
        {
            if (enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("等待条自动启用必须是布尔值。");
            enabled = enabledValue.GetBoolean();
        }
        var threshold = 30;
        if (profile.TryGetProperty("rareGuestParticipationAutoResumeThresholdPercent", out var thresholdValue)
            && (thresholdValue.ValueKind != JsonValueKind.Number || !thresholdValue.TryGetInt32(out threshold) || threshold is < 1 or > 100))
            throw new InvalidDataException("等待条自动启用阈值必须是 1 至 100 的整数。");
        var front = false;
        if (profile.TryGetProperty("rareGuestParticipationAutoResumePosition", out var position))
        {
            if (position.ValueKind != JsonValueKind.String || position.GetString() is not ("front" or "tail"))
                throw new InvalidDataException("等待条自动启用位置必须是 front 或 tail。");
            front = position.GetString() == "front";
        }
        return new(revision, moduleEnabled && enabled, threshold, front);
    }

    /// <summary>
    /// 配置存储已完成 schema 迁移和校验，此处仍独立拒绝无序、重复、负数和超长名单，
    /// 防止运行时调用绕过存储层。缺失字段仅表示旧内部离线夹具的禁用默认值。
    /// </summary>
    private static int[] ParseManagedGuestIds(JsonElement profile)
    {
        if (!profile.TryGetProperty("managedRareGuestIds", out var values)) return Array.Empty<int>();
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 512)
            throw new InvalidDataException("稀客调度名单必须是最多 512 项的数组。");
        var result = new int[values.GetArrayLength()];
        var previous = -1; var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (!value.TryGetInt32(out var id) || id < 0 || id <= previous)
                throw new InvalidDataException("稀客调度名单必须是非负且严格递增的整数。");
            result[index++] = id; previous = id;
        }
        return result;
    }
    public static RuntimeAutomationControlDecision Observe(
        RuntimeAutomationControlTargetKind targetKind,
        RuntimeAutomationControlStage stage,
        bool forceStageConfiguration,
        DateTime nowUtc)
    {
        lock (SyncRoot)
        {
            return Evaluate(targetKind, stage, forceStageConfiguration, nowUtc.ToUniversalTime());
        }
    }

    public static RuntimeAutomationControlPermit AcquirePermit(
        RuntimeAutomationControlTargetKind targetKind,
        RuntimeAutomationControlStage stage,
        bool forceStageConfiguration,
        DateTime nowUtc)
    {
        Monitor.Enter(SyncRoot);
        try
        {
            var decision = Evaluate(
                targetKind,
                stage,
                forceStageConfiguration,
                nowUtc.ToUniversalTime());
            if (!decision.Allowed)
            {
                Monitor.Exit(SyncRoot);
                return new RuntimeAutomationControlPermit(decision, release: null);
            }

            return new RuntimeAutomationControlPermit(
                decision,
                () => Monitor.Exit(SyncRoot));
        }
        catch
        {
            Monitor.Exit(SyncRoot);
            throw;
        }
    }

    private static RuntimeAutomationControlDecision Evaluate(
        RuntimeAutomationControlTargetKind targetKind,
        RuntimeAutomationControlStage stage,
        bool forceStageConfiguration,
        DateTime nowUtc)
    {
        if (_resourceReady?.Invoke() == false)
            return SuspendAuthority("game-media-not-ready",
                "游戏图片正在初始化或重新读取；已开始的料理保留在原厨具，等待图片、主设备连接与配置就绪。",
                deliveryConfigured: false, completionConfigured: false);
        var profile = _profile;
        if (profile == null || _authorityRevision <= 0)
        {
            return SuspendAuthority(
                "automation-profile-unavailable",
                "尚未取得主设备的自动化配置；已开始的料理会保留在原厨具，等待配置同步完成。",
                deliveryConfigured: false,
                completionConfigured: false);
        }

        var deliveryConfigured = forceStageConfiguration || targetKind switch
        {
            RuntimeAutomationControlTargetKind.Rare => profile.AutoPrepCollectCooking,
            RuntimeAutomationControlTargetKind.Normal => profile.AutoNormalDeliverFood,
            _ => false,
        };
        var completionConfigured = forceStageConfiguration || targetKind switch
        {
            RuntimeAutomationControlTargetKind.Rare => profile.AutoPrepCompleteOrder,
            RuntimeAutomationControlTargetKind.Normal => profile.AutoNormalCompleteOrder,
            _ => false,
        };

        if (_leaseAuthorityRevision != _authorityRevision)
        {
            return SuspendAuthority(
                string.IsNullOrWhiteSpace(_leaseBlockReasonCode)
                    ? "automation-authority-unavailable"
                    : _leaseBlockReasonCode,
                string.IsNullOrWhiteSpace(_leaseBlockMessage)
                    ? "正在同步自动化主设备；已开始的料理会保留在原厨具，恢复同步后继续。"
                    : _leaseBlockMessage,
                deliveryConfigured,
                completionConfigured);
        }

        if (_leaseExpiresAtUtc <= nowUtc)
        {
            return SuspendAuthority(
                "automation-lease-expired",
                "主设备的自动化状态未及时更新，已暂停；已开始的料理会保留在原厨具，恢复同步后继续。",
                deliveryConfigured,
                completionConfigured);
        }

        if (!profile.AutomationEnabled)
        {
            return SuspendConfiguration(
                "automation-disabled",
                "自动化总控已关闭；已开始的料理会保留在原厨具，重新开启后继续。",
                deliveryConfigured,
                completionConfigured);
        }

        var targetEnabled = targetKind switch
        {
            RuntimeAutomationControlTargetKind.Rare => profile.AutoRareOrderEnabled,
            RuntimeAutomationControlTargetKind.Normal => profile.AutoNormalOrderEnabled,
            _ => false,
        };
        if (!targetEnabled)
        {
            var targetLabel = targetKind == RuntimeAutomationControlTargetKind.Rare ? "稀客" : "普客";
            return SuspendConfiguration(
                targetKind == RuntimeAutomationControlTargetKind.Rare
                    ? "rare-automation-disabled"
                    : "normal-automation-disabled",
                $"{targetLabel}自动化模块已关闭；该模块已开始的料理会保留在原厨具，重新开启后继续。",
                deliveryConfigured,
                completionConfigured);
        }

        if (stage is RuntimeAutomationControlStage.FoodDelivery
            or RuntimeAutomationControlStage.YuumaSettlement
            && !deliveryConfigured)
        {
            return SuspendConfiguration(
                targetKind == RuntimeAutomationControlTargetKind.Rare
                    ? "rare-food-delivery-disabled"
                    : "normal-food-delivery-disabled",
                "自动送达料理已关闭；制作会继续，成品会保留在原厨具，重新开启后从送达步骤继续。",
                deliveryConfigured,
                completionConfigured);
        }

        if (stage is RuntimeAutomationControlStage.OrderEvaluation
            or RuntimeAutomationControlStage.YuumaSettlement
            && !completionConfigured)
        {
            return SuspendConfiguration(
                targetKind == RuntimeAutomationControlTargetKind.Rare
                    ? "rare-order-completion-disabled"
                    : "normal-order-completion-disabled",
                "自动完成订单已关闭；已提交的料理不会重复送达，重新开启后从订单评价步骤继续。",
                deliveryConfigured,
                completionConfigured);
        }

        return new RuntimeAutomationControlDecision(
            true,
            "active",
            "",
            "",
            _authorityRevision,
            deliveryConfigured,
            completionConfigured);
    }

    private static RuntimeAutomationControlDecision SuspendAuthority(
        string reasonCode,
        string message,
        bool deliveryConfigured,
        bool completionConfigured)
    {
        return new RuntimeAutomationControlDecision(
            false,
            "suspended-authority",
            reasonCode,
            message,
            _authorityRevision,
            deliveryConfigured,
            completionConfigured);
    }

    private static RuntimeAutomationControlDecision SuspendConfiguration(
        string reasonCode,
        string message,
        bool deliveryConfigured,
        bool completionConfigured)
    {
        return new RuntimeAutomationControlDecision(
            false,
            "suspended-configuration",
            reasonCode,
            message,
            _authorityRevision,
            deliveryConfigured,
            completionConfigured);
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }

    private sealed record RuntimeAutomationControlProfile(
        bool AutomationEnabled,
        bool AutoRareOrderEnabled,
        bool AutoNormalOrderEnabled,
        bool AutoPrepCollectCooking,
        bool AutoPrepCompleteOrder,
        bool AutoNormalDeliverFood,
        bool AutoNormalCompleteOrder)
    {
        public static RuntimeAutomationControlProfile Parse(JsonElement profile)
        {
            if (profile.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Companion automation profile must be a JSON object.");
            }

            return new RuntimeAutomationControlProfile(
                ReadBool(profile, "automationEnabled"),
                ReadBool(profile, "autoRareOrderEnabled"),
                ReadBool(profile, "autoNormalOrderEnabled"),
                ReadBool(profile, "autoPrepCollectCooking"),
                ReadBool(profile, "autoPrepCompleteOrder"),
                ReadBool(profile, "autoNormalDeliverFood"),
                ReadBool(profile, "autoNormalCompleteOrder"));
        }

        private static bool ReadBool(JsonElement profile, string name)
        {
            if (!profile.TryGetProperty(name, out var value)
                || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new InvalidDataException($"Companion automation profile field '{name}' is not a boolean.");
            }

            return value.GetBoolean();
        }
    }
}
