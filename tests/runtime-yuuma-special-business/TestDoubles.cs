namespace MystiaStewardCompanion.Save
{
    internal interface IRuntimeCastTestSource
    {
        object? TryCast(string targetTypeName);
    }

    internal static class RuntimeReflectionUtility
    {
        private static readonly List<string> CastTargets = new();

        public static IReadOnlyList<string> RecordedCastTargets => CastTargets;

        public static void ResetRecordedCasts()
        {
            CastTargets.Clear();
        }

        public static object? TryCastRuntimeObject(object? value, string targetTypeName)
        {
            CastTargets.Add(targetTypeName);
            return value is IRuntimeCastTestSource source
                ? source.TryCast(targetTypeName)
                : null;
        }
    }

    internal interface ISpecialBusinessOrderModule
    {
        bool MatchesChallenge(string challengeType);

        SpecialBusinessOrderClassification Classify(
            string challengeType,
            object? order,
            object? controller,
            string source);
    }

    internal sealed record SpecialBusinessOrderClassification(
        bool AutomationAllowed,
        string Role,
        string RoleLabel,
        string AutomationBlockReason,
        int? RuntimeGuestId)
    {
        public static SpecialBusinessOrderClassification Standard { get; } = new(
            AutomationAllowed: true,
            Role: "",
            RoleLabel: "",
            AutomationBlockReason: "",
            RuntimeGuestId: null);
    }

    internal static class SpecialBusinessModuleRegistry
    {
        public static SpecialBusinessOrderClassification AllowedSpecialOrder(
            string role,
            string label,
            int? runtimeGuestId = null)
        {
            return new SpecialBusinessOrderClassification(
                AutomationAllowed: true,
                Role: role,
                RoleLabel: label,
                AutomationBlockReason: "",
                RuntimeGuestId: runtimeGuestId);
        }

        public static SpecialBusinessOrderClassification Blocked(string role, string label, string reason)
        {
            return new SpecialBusinessOrderClassification(
                AutomationAllowed: false,
                Role: role,
                RoleLabel: label,
                AutomationBlockReason: reason,
                RuntimeGuestId: null);
        }
    }

    internal static class SpecialBusinessDiagnostics
    {
        public static void AppendYuumaOrderClassification(
            string challengeType,
            SpecialBusinessOrderClassification classification,
            YuumaChallengeOrderIdentity identity,
            object? order,
            object? controller,
            string source)
        {
        }
    }
}

namespace GameData.Core.Collections.NightSceneUtility
{
    public class GuestBase
    {
        public GuestBase(int id)
        {
            Id = id;
        }

        public int Id { get; }
    }

    public sealed class NamedGuest : GuestBase
    {
        public NamedGuest(int id, string name)
            : base(id)
        {
            Name = name;
        }

        public string Name { get; }
    }
}

namespace NightScene.GuestManagementUtility
{
    public static class GuestsManager
    {
        public sealed class OrderBase : MystiaStewardCompanion.Save.IRuntimeCastTestSource
        {
            private const string NormalOrderTypeName = "NightScene.GuestManagementUtility.GuestsManager+NormalOrder";
            private const string SpecialOrderTypeName = "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder";

            public OrderBase(object? normalOrder, object? specialOrder)
            {
                NormalOrderValue = normalOrder;
                SpecialOrderValue = specialOrder;
            }

            public object? NormalOrderValue { get; }

            public object? SpecialOrderValue { get; }

            public object? TryCast(string targetTypeName)
            {
                return targetTypeName switch
                {
                    NormalOrderTypeName => NormalOrderValue,
                    SpecialOrderTypeName => SpecialOrderValue,
                    _ => null,
                };
            }
        }

        public sealed class NormalOrder
        {
            public NormalOrder(object guest)
            {
                Guest = guest;
            }

            public object Guest { get; }
        }

        public sealed class SpecialOrder
        {
            public SpecialOrder(object specialGuests)
            {
                SpecialGuests = specialGuests;
            }

            public object SpecialGuests { get; }
        }
    }
}

internal sealed class OrderController
{
    public OrderController(object orderingGuest)
    {
        OrderingGuest = orderingGuest;
    }

    public object OrderingGuest { get; }
}

internal sealed class MissingOrderingGuestController
{
}

internal sealed class AliasGuest
{
    public AliasGuest(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }

    public string Name { get; }
}

internal sealed class AliasOrder
{
    public AliasOrder(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
