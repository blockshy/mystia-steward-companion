using System.Reflection;

/// <summary>精确类型名桥接夹具的原生对象身份；不包含排序或辅助判断。</summary>
internal abstract class NativeProbe
{
    private static long _identity = 10000;
    public nint Pointer { get; set; } = (nint)Interlocked.Increment(ref _identity);
}

namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public readonly System.Collections.Generic.List<string> Warnings = new();
        public void LogWarning(object value) => Warnings.Add(value.ToString() ?? "");
    }
}

namespace HarmonyLib
{
    public static class Priority { public const int Last = 0; }
    public sealed class HarmonyMethod
    {
        public readonly MethodInfo Method;
        public int priority;
        public string[] after = Array.Empty<string>();
        public HarmonyMethod(MethodInfo method) => Method = method;
    }
    public sealed class Harmony
    {
        public static readonly System.Collections.Generic.List<(MethodInfo Original, HarmonyMethod Postfix)> Registrations = new();
        public Harmony(string id) { }
        // 只记录生产注册形态，不修改运行时IL，因此后续明确直接调用生产postfix。
        public void Patch(MethodInfo original, HarmonyMethod? postfix = null)
            => Registrations.Add((original, postfix ?? throw new ArgumentNullException(nameof(postfix))));
    }
}

namespace Il2CppSystem
{
    public delegate TResult Func<in T, out TResult>(T argument);
    // #783将原生装箱值类型映射成CLR引用类；不能用CLR struct模拟，否则会掩盖泛型封送差异。
    internal class Object : NativeProbe { }
    internal class ValueType : Object { }
}

namespace Il2CppSystem.Collections.Generic
{
    public interface IEnumerable<out T> : System.Collections.Generic.IEnumerable<T> { }
    internal sealed class KeyValuePair<TKey, TValue> : Il2CppSystem.ValueType
    {
        public TKey Key { get; }
        public TValue Value { get; }
        public KeyValuePair(TKey key, TValue value) { Key = key; Value = value; }
    }
    // FullName、索引器、Count、Key、Value与#783对应；集合内容仍为普通托管值，以便观察写入。
    internal sealed class List<T> : NativeProbe, IEnumerable<T>
    {
        public System.Collections.Generic.List<T> Values { get; } = new();
        public int Count => Values.Count;
        public int Writes { get; private set; }
        public int GenericWrites { get; private set; }
        public int BoxedWrites { get; private set; }
        public bool InvalidBoxPayload { get; private set; }
        public bool ThrowNextWrite;
        public bool IgnoreNextWrite;
        public System.Action? Reading;
        public T this[int index]
        {
            get
            {
                Reading?.Invoke();
                // 原生错误写入会把盒头当作Key/Value，之后读取身份失败；在CLR内用确定异常表达，
                // 不创建真实坏指针或让测试进程发生访问冲突。
                if (InvalidBoxPayload) throw new InvalidOperationException("native item type/pointer/id is invalid");
                return Values[index];
            }
            set
            {
                GenericWrites++;
                if (value is Il2CppSystem.ValueType)
                {
                    Writes++;
                    InvalidBoxPayload = true;
                    return;
                }
                WriteValidated(index, value);
            }
        }

        // 与#783的真实方法MetadataName完全相同；参数按Object契约传入盒对象，由原生端检查/拆箱。
        public void System_Collections_IList_set_Item(int index, Il2CppSystem.Object value)
        {
            BoxedWrites++;
            if (value is not T typed) throw new ArgumentException("boxed item is not the exact native element type");
            WriteValidated(index, typed);
        }

        private void WriteValidated(int index, T value)
        {
            Writes++;
            if (ThrowNextWrite) { ThrowNextWrite = false; throw new InvalidOperationException("native-shaped setter failure"); }
            if (IgnoreNextWrite) { IgnoreNextWrite = false; return; }
            Values[index] = value;
        }
        public System.Collections.Generic.IEnumerator<T> GetEnumerator() => Values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

namespace GameData.Core.Collections
{
    internal sealed class Ingredient : NativeProbe { public int id { get; set; } }
    internal sealed class Sellable : NativeProbe { public int id { get; set; } }
    internal sealed class Recipe : NativeProbe { public int id { get; set; } }
}

namespace NightScene.UI.CookingUtility
{
    using Il2CppSystem.Collections.Generic;
    using GameData.Core.Collections;
    internal sealed class WorkSceneCookingSelectionPannel : NativeProbe
    {
        public List<KeyValuePair<Ingredient, int>> m_Ingredient_SeaFoodInstances { get; set; } = new();
        public List<KeyValuePair<Ingredient, int>> m_Ingredient_MeatInstances { get; set; } = new();
        public List<KeyValuePair<Ingredient, int>> m_Ingredient_VeggiesInsatance { get; set; } = new();
        public List<KeyValuePair<Ingredient, int>> m_Ingredient_OtherInstances { get; set; } = new();
        public List<Recipe> m_RecipeInstances { get; set; } = new();
        public void UpdateRecipeField() { }
    }
    internal sealed class WorkSceneStoragePannel : NativeProbe
    {
        public List<KeyValuePair<Sellable, int>> m_Beverages { get; set; } = new();
        public int openType { get; set; } = 1;
        public void UpdateBevField() { }
    }
}

namespace GameData.RunTime.Common
{
    using Il2CppSystem.Collections.Generic;
    using GameData.Core.Collections;
    internal static class RunTimeAlbum
    {
        public enum DataSource { All = 0, InStorageOnly = 1 }
        public static void ClassifyIngredientByType(DataSource source, List<KeyValuePair<Ingredient, int>> seafood,
            List<KeyValuePair<Ingredient, int>> meat, List<KeyValuePair<Ingredient, int>> veggies,
            List<KeyValuePair<Ingredient, int>> other,
            Il2CppSystem.Func<List<KeyValuePair<Ingredient, int>>, IEnumerable<KeyValuePair<Ingredient, int>>>? filter = null) { }
    }
}

namespace MystiaStewardCompanion.Save
{
    internal enum RuntimeUiListSurfaceKind { Cooking, Beverage }
    internal readonly record struct NightLifecycle(bool IsActive, long Generation, int ThreadId);
    internal static class RuntimeNightBusinessLifecycle
    {
        internal static NightLifecycle Snapshot = new(true, 1, Environment.CurrentManagedThreadId);
    }
    internal static class RuntimeReflectionUtility
    {
        internal static Type? FindType(string name) => typeof(RuntimeReflectionUtility).Assembly.GetType(name);
        internal static object? GetMemberValue(object value, string name)
            => value.GetType().GetProperty(name)?.GetValue(value) ?? value.GetType().GetField(name)?.GetValue(value);
        internal static bool TryReadNativeObjectPointer(object? value, out nint pointer)
        { pointer = value is NativeProbe native ? native.Pointer : 0; return pointer != 0; }
    }
    internal sealed class ProbePublicationLease : IDisposable { public void Dispose() { } }
    internal static class RuntimeUiPinningService
    {
        internal static object? Panel;
        internal static RuntimeUiTargetSetSnapshot Target = RuntimeUiTargetSetSnapshot.Disabled;
        internal static RuntimeUiListSurfaceKind Kind;
        internal static bool Current = true;
        internal static bool AllowLease = true;
        // 生产作用域及主权威校验由ui-pinning-runtime独立测试；这里提供严格可观察的边界契约。
        internal static bool TryGetListOrderContext(RuntimeUiListSurfaceKind kind, object? expected,
            out object panel, out RuntimeUiTargetSetSnapshot target)
        {
            panel = Panel!; target = Target;
            return Current && Panel != null && Kind == kind && (expected == null || ReferenceEquals(expected, Panel))
                && (Panel is not NightScene.UI.CookingUtility.WorkSceneStoragePannel storage || storage.openType == 1);
        }
        internal static bool TryAcquireTargetPublicationLease(long generation, out ProbePublicationLease lease)
        { lease = new(); return AllowLease && generation == RuntimeNightBusinessLifecycle.Snapshot.Generation; }
        internal static bool IsSurfaceRefreshTargetCurrentOrDeferred(RuntimeUiTargetSetSnapshot expected)
            => Current && ReferenceEquals(Target, expected);
    }
    internal static class RuntimeTargetRecipeVariantService
    {
        internal const string HarmonyId = "com.tyukki.mystia-steward-companion.runtime-target-recipe-variant";
        internal static readonly System.Collections.Generic.Dictionary<nint, RuntimeUiTargetKinds> Claims = new();
        internal static bool Accept = true;
        // 真实变体映射另在runtime-target-recipe-variant验证；这里只模拟该精确映射的输入边界。
        internal static bool TryGetRecipeListOrderClaims(object panel, object recipeList,
            RuntimeUiTargetSetSnapshot target, nint pointer, int id, out RuntimeUiTargetKinds claims)
        {
            claims = Claims.TryGetValue(pointer, out var value) ? value : target.GetBaseRecipeClaims(id);
            return Accept && ReferenceEquals(panel, RuntimeUiPinningService.Panel);
        }
    }
}
