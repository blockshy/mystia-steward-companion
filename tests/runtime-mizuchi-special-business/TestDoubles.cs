namespace MystiaStewardCompanion.Save
{
    internal interface IRuntimeCastTestSource
    {
        object? TryCast(string targetTypeName);
    }

    internal static class RuntimeReflectionUtility
    {
        public static object? TryCastRuntimeObject(object? value, string targetTypeName)
        {
            if (value != null && string.Equals(value.GetType().FullName, targetTypeName, StringComparison.Ordinal))
            {
                return value;
            }

            return value is IRuntimeCastTestSource source
                ? source.TryCast(targetTypeName)
                : null;
        }

        public static bool TryReadNativeObjectPointer(object? value, out nint pointer)
        {
            pointer = value is Il2CppSystem.Object native ? native.Pointer : 0;
            return pointer != 0;
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
            return new SpecialBusinessOrderClassification(true, role, label, "", runtimeGuestId);
        }

        public static SpecialBusinessOrderClassification Blocked(string role, string label, string reason)
        {
            return new SpecialBusinessOrderClassification(false, role, label, reason, null);
        }
    }

    internal static class SpecialBusinessDiagnostics
    {
        public static void AppendMizuchiOrderClassification(
            string challengeType,
            SpecialBusinessOrderClassification classification,
            MizuchiOrderIdentity identity,
            object? order,
            object? controller,
            string source)
        {
        }
    }
}

namespace Il2CppSystem
{
    public class Object
    {
        private static long _nextPointer = 0x1000;

        public Object()
        {
            Pointer = new IntPtr(Interlocked.Increment(ref _nextPointer));
        }

        public IntPtr Pointer { get; set; }
    }

    public class Delegate : Object
    {
        public Delegate(Object target, Reflection.MethodInfo method)
        {
            Target = target;
            Method = method;
        }

        public Reflection.MethodInfo Method { get; }

        public Object Target { get; }
    }

    public class MulticastDelegate : Delegate
    {
        protected MulticastDelegate(Object target, Reflection.MethodInfo method)
            : base(target, method)
        {
        }

        public virtual Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Delegate> GetInvocationList()
        {
            return new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Delegate>(new Delegate[] { this });
        }
    }
}

namespace Il2CppSystem.Reflection
{
    public class MemberInfo : Il2CppSystem.Object
    {
        public MemberInfo(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    public sealed class MethodInfo : MemberInfo
    {
        public MethodInfo(string name)
            : base(name)
        {
        }
    }
}

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppReferenceArray<T> : Il2CppSystem.Object
    {
        private readonly T[] _items;

        public Il2CppReferenceArray(T[] items)
        {
            _items = items;
        }

        public int Length => _items.Length;

        public T this[int index] => _items[index];
    }
}

namespace GameData.Core.Collections.NightSceneUtility
{
    public class GuestBase : Il2CppSystem.Object
    {
        public GuestBase(int id)
        {
            Id = id;
        }

        public int Id { get; }
    }

    public sealed class SpecialGuest : GuestBase
    {
        public SpecialGuest(int id)
            : base(id)
        {
        }
    }
}

namespace NightScene.GuestManagementUtility
{
    using GameData.Core.Collections.NightSceneUtility;
    using Il2CppInterop.Runtime.InteropTypes.Arrays;

    public class GuestGroupController : Il2CppSystem.Object, MystiaStewardCompanion.Save.IRuntimeCastTestSource
    {
        private readonly SpecialGuestsController? _specialGuestsControllerCast;

        public GuestGroupController(
            GuestBase orderingGuest,
            SpecialGuestsController? specialGuestsControllerCast = null)
        {
            OrderingGuest = orderingGuest;
            _specialGuestsControllerCast = specialGuestsControllerCast;
        }

        public GuestBase OrderingGuest { get; }

        public OverrideEvalResultDelegate? OverrideEvaluationCallback { get; set; }

        public object? TryCast(string targetTypeName)
        {
            return string.Equals(
                targetTypeName,
                "NightScene.GuestManagementUtility.SpecialGuestsController",
                StringComparison.Ordinal)
                ? _specialGuestsControllerCast
                : null;
        }

        public sealed class OverrideEvalResultDelegate : Il2CppSystem.MulticastDelegate
        {
            private readonly Il2CppSystem.Delegate[] _invocations;

            public OverrideEvalResultDelegate(
                Il2CppSystem.Object target,
                string methodName = "<MainChallengeLoop>g__GroupOverrideEvaluationCallback|74",
                int invocationCount = 1)
                : base(target, new Il2CppSystem.Reflection.MethodInfo(methodName))
            {
                _invocations = invocationCount switch
                {
                    0 => Array.Empty<Il2CppSystem.Delegate>(),
                    1 => new Il2CppSystem.Delegate[] { this },
                    _ => Enumerable.Repeat<Il2CppSystem.Delegate>(this, invocationCount).ToArray(),
                };
            }

            public override Il2CppReferenceArray<Il2CppSystem.Delegate> GetInvocationList()
            {
                return new Il2CppReferenceArray<Il2CppSystem.Delegate>(_invocations);
            }
        }
    }

    public sealed class SpecialGuestsController : GuestGroupController
    {
        public SpecialGuestsController(SpecialGuest specialGuest)
            : base(specialGuest)
        {
            SpecialGuest = specialGuest;
        }

        public SpecialGuest SpecialGuest { get; }

        public GuestGroupController AsBaseWrapper()
        {
            return new GuestGroupController(OrderingGuest, this)
            {
                Pointer = Pointer,
                OverrideEvaluationCallback = OverrideEvaluationCallback,
            };
        }
    }

    public static class GuestsManager
    {
        public sealed class OrderBase : Il2CppSystem.Object, MystiaStewardCompanion.Save.IRuntimeCastTestSource
        {
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
                    "NightScene.GuestManagementUtility.GuestsManager+NormalOrder" => NormalOrderValue,
                    "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder" => SpecialOrderValue,
                    _ => null,
                };
            }
        }

        public sealed class NormalOrder : Il2CppSystem.Object
        {
            public NormalOrder(GuestBase guest)
            {
                Guest = guest;
            }

            public GuestBase Guest { get; }
        }

        public sealed class SpecialOrder : Il2CppSystem.Object
        {
            public SpecialOrder(SpecialGuest specialGuests)
            {
                SpecialGuests = specialGuests;
            }

            public SpecialGuest SpecialGuests { get; }
        }
    }
}

namespace GameData.Profile
{
    using NightScene.GuestManagementUtility;

    public sealed class DLC5_MizuchiChallengeBossData
    {
        public enum MizuchiControlType
        {
            WrongBeverageTag,
            WrongFoodOrder,
            WrongTalkingDialog,
            None,
        }

        public sealed class __c__DisplayClass66_0 : Il2CppSystem.Object
        {
            public int catchMizuchiNum { get; set; }

            public int currentGuestWhoIsControlledByMizuchi { get; set; }

            public MizuchiControlType typeOfMizuchi { get; set; }

            public int targetIngredientId { get; set; }

            public bool isMizuchiChallenge { get; set; }

            public int needCatchMizuchiTime { get; set; }
        }

        public sealed class __c__DisplayClass66_9 : Il2CppSystem.Object
        {
            public int selectedGuestGroup { get; set; }

            public SpecialGuestsController? group { get; set; }

            public __c__DisplayClass66_0? field_Public___c__DisplayClass66_0_0 { get; set; }
        }
    }
}
