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
    private static long _authorityRevision;
    private static long _leaseAuthorityRevision;
    private static DateTime _leaseExpiresAtUtc;
    private static string _leaseBlockReasonCode = "automation-authority-unavailable";
    private static string _leaseBlockMessage = "自动化主设备控制权尚未就绪。";

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
        lock (SyncRoot)
        {
            var changed = _authorityRevision != authorityRevision || _profile != profile;
            _profile = profile;
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
            _authorityRevision = 0;
            _leaseAuthorityRevision = 0;
            _leaseExpiresAtUtc = DateTime.MinValue;
            _leaseBlockReasonCode = "automation-authority-unavailable";
            _leaseBlockMessage = RequireText(message, nameof(message));
        }
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
        var profile = _profile;
        if (profile == null || _authorityRevision <= 0)
        {
            return SuspendAuthority(
                "automation-profile-unavailable",
                "尚未取得主设备的生效自动化配置；已开始的料理会保留在原厨具，等待权威配置就绪。",
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
                    ? "自动化主设备控制权正在切换；已开始的料理会保留在原厨具，取得新控制权后继续。"
                    : _leaseBlockMessage,
                deliveryConfigured,
                completionConfigured);
        }

        if (_leaseExpiresAtUtc <= nowUtc)
        {
            return SuspendAuthority(
                "automation-lease-expired",
                "主设备自动化租约已过期；已开始的料理会保留在原厨具，续约成功后继续。",
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
