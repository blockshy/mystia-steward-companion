using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private static void DeliverMatchedRareOrderPart(
        OrderPreparationResult result,
        object tray,
        object? sellable,
        bool shouldDeliver,
        string stepName,
        string message)
    {
        if (!shouldDeliver || sellable == null) return;
        InvokeInstance(tray, "Deliver", new object?[] { sellable });
        result.Steps.Add(new OrderPreparationStep
        {
            Name = stepName,
            Ok = true,
            Message = message,
        });
    }

    private static object? FindRareOrderFoodInTray(
        IReadOnlyList<object> trayItems,
        int expectedFoodId,
        IReadOnlyList<int> acceptableFoodIds,
        TimeSpan backlogThreshold,
        out int matchedFoodId,
        out bool matchedBacklogFood,
        out int matchedBacklogAgeSeconds)
    {
        matchedFoodId = expectedFoodId;
        matchedBacklogFood = false;
        matchedBacklogAgeSeconds = 0;

        var exact = trayItems.FirstOrDefault(item => IsSellable(item, sellableType: 0, id: expectedFoodId));
        if (exact != null)
        {
            return exact;
        }

        var acceptable = acceptableFoodIds
            .Where(id => id >= 0 && id != expectedFoodId)
            .Distinct()
            .ToHashSet();
        if (acceptable.Count == 0) return null;

        foreach (var item in trayItems)
        {
            if (ReadSellableType(item) != 0) continue;

            var foodId = ReadSellableId(item);
            if (!acceptable.Contains(foodId)) continue;
            if (!TryGetTrayObservationAge(item, out var age) || age < backlogThreshold) continue;

            matchedFoodId = foodId;
            matchedBacklogFood = true;
            matchedBacklogAgeSeconds = Math.Max(0, (int)Math.Floor(age.TotalSeconds));
            return item;
        }

        return null;
    }

    private static string FormatRareFoodDeliveryMessage(
        string recipeName,
        int expectedFoodId,
        int matchedFoodId,
        bool matchedBacklogFood,
        string suffix)
    {
        if (!matchedBacklogFood)
        {
            return $"{recipeName} {suffix}";
        }

        return $"已复用送餐盘中堆积料理 #{matchedFoodId}（原目标料理 #{expectedFoodId}：{recipeName}），{suffix}";
    }

    private static void RefreshTrayObservations(IReadOnlyList<object> trayItems)
    {
        var now = DateTime.UtcNow;
        var currentKeys = new HashSet<string>();

        lock (TrayObservationLock)
        {
            foreach (var item in trayItems)
            {
                var key = BuildTrayObservationKey(item);
                currentKeys.Add(key);
                if (!TrayObservationFirstSeen.ContainsKey(key))
                {
                    TrayObservationFirstSeen[key] = now;
                }
            }

            foreach (var key in TrayObservationFirstSeen.Keys.ToArray())
            {
                if (currentKeys.Contains(key)) continue;
                if (now - TrayObservationFirstSeen[key] <= TrayObservationRetention) continue;
                TrayObservationFirstSeen.Remove(key);
            }
        }
    }

    private static bool TryGetTrayObservationAge(object item, out TimeSpan age)
    {
        var key = BuildTrayObservationKey(item);
        lock (TrayObservationLock)
        {
            if (TrayObservationFirstSeen.TryGetValue(key, out var firstSeen))
            {
                age = DateTime.UtcNow - firstSeen;
                return true;
            }
        }

        age = TimeSpan.Zero;
        return false;
    }

    private static string BuildTrayObservationKey(object item)
    {
        var type = ReadSellableType(item);
        var id = ReadSellableId(item);
        try
        {
            return $"{type}:{id}:ptr:{ReadObjectPointer(item):x}";
        }
        catch
        {
            return $"{type}:{id}:hash:{RuntimeHelpers.GetHashCode(item)}";
        }
    }

    private static (bool Ok, string Message) TryTakeBeverageToTray(int beverageId, string beverageName)
    {
        var tray = GetSingletonInstance(IzakayaTrayTypeName);
        if (tray == null)
        {
            return (false, "当前送餐盘对象不可用，请确认已进入夜晚经营页面。");
        }

        if (ReadTrayItems(tray).Any(item => IsSellable(item, sellableType: 1, id: beverageId)))
        {
            return (true, $"{beverageName} 已在送餐盘中，本次不重复取酒。");
        }

        var currentQuantity = GetBeverageQuantity(beverageId);
        if (currentQuantity == 0)
        {
            return (false, $"{beverageName} 当前库存为 0，无法放入送餐盘。");
        }

        var isFull = InvokeInstance(tray, "get_IsTrayFull", Array.Empty<object?>());
        if (isFull is bool isTrayFull && isTrayFull)
        {
            return (false, "送餐盘已满，无法继续取酒。");
        }

        var sellable = InvokeStatic(DataBaseCoreTypeName, "AsNewBeverage", new object?[] { beverageId });
        if (sellable == null)
        {
            return (false, $"无法从游戏数据库创建酒水对象：{beverageName} #{beverageId}。");
        }

        InvokeInstance(tray, "Receive", new[] { sellable });
        if (currentQuantity > 0)
        {
            InvokeRuntimeStorageOut("BeverageOut", beverageId);
        }

        var quantityText = currentQuantity < 0 ? "无限库存" : $"剩余 {Math.Max(0, currentQuantity - 1)}";
        return (true, $"{beverageName} 已放入送餐盘（{quantityText}）。");
    }

    private static Delegate? CreateTrayReceiveDelegate(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke");
        var parameter = invoke?.GetParameters().FirstOrDefault();
        if (parameter == null) return null;

        var method = typeof(RuntimeOrderPreparationService)
            .GetMethod(nameof(ReceiveCookedFoodGeneric), BindingFlags.NonPublic | BindingFlags.Static)
            ?.MakeGenericMethod(parameter.ParameterType);
        return method == null ? null : Delegate.CreateDelegate(delegateType, method);
    }

    private static void ReceiveCookedFoodGeneric<T>(T sellable)
    {
        if (sellable == null) return;
        var tray = GetSingletonInstance(IzakayaTrayTypeName);
        if (tray == null) return;
        InvokeInstance(tray, "Receive", new object?[] { sellable });
    }

    private static IEnumerable<object> ReadTrayItems(object tray)
    {
        var trayList = InvokeInstance(tray, "get_Tray", Array.Empty<object?>());
        if (trayList == null) yield break;

        var seen = new HashSet<nint>();
        var slotCount = ToInt(TryInvokeInstanceValue(tray, "get_TrayMaxNum"));
        if (slotCount <= 0)
        {
            slotCount = ReadFixedListCapacity(trayList);
        }

        if (slotCount <= 0)
        {
            slotCount = ToInt(TryInvokeInstanceValue(trayList, "Count"));
        }

        for (var index = 0; index < Math.Min(slotCount, 32); index++)
        {
            var item = TryInvokeInstanceValue(trayList, "get_Item", new object?[] { index });
            if (item == null) continue;

            nint pointer;
            try
            {
                pointer = ReadObjectPointer(item);
            }
            catch
            {
                pointer = new IntPtr(RuntimeHelpers.GetHashCode(item));
            }

            if (!seen.Add(pointer)) continue;
            yield return item;
        }

        foreach (var item in ReadObjectEnumerable(trayList))
        {
            nint pointer;
            try
            {
                pointer = ReadObjectPointer(item);
            }
            catch
            {
                pointer = new IntPtr(RuntimeHelpers.GetHashCode(item));
            }

            if (!seen.Add(pointer)) continue;
            yield return item;
        }
    }

    private static int ReadFixedListCapacity(object fixedList)
    {
        var elements = ReadMember(fixedList, "elements");
        if (elements is Array array) return array.Length;

        var length = ReadMember(elements ?? fixedList, "Length")
            ?? ReadMember(elements ?? fixedList, "Count")
            ?? ReadMember(elements ?? fixedList, "max_length")
            ?? ReadMember(elements ?? fixedList, "maxLength");
        return ToInt(length);
    }

    private static bool IsSellable(object item, int sellableType, int id)
    {
        return ReadSellableType(item) == sellableType && ReadSellableId(item) == id;
    }

    private static string FormatTraySummary(IReadOnlyList<object> trayItems)
    {
        if (trayItems.Count == 0)
        {
            return "当前读取到的送餐盘为空。";
        }

        var items = trayItems
            .Take(8)
            .Select(item => $"type={ReadSellableType(item)},id={ReadSellableId(item)}")
            .ToArray();
        var suffix = trayItems.Count > items.Length ? $" 等 {trayItems.Count} 个" : "";
        return $"当前读取到的送餐盘：{string.Join("; ", items)}{suffix}。";
    }

    private static int ReadSellableType(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_Type") ?? ReadMember(item, "Type");
        return ToInt(value);
    }

    private static int ReadSellableId(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_id")
            ?? TryInvokeInstanceValue(item, "get_Id")
            ?? ReadMember(item, "id")
            ?? ReadMember(item, "Id");
        return ToInt(value);
    }
}
