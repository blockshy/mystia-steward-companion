using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    /// <summary>
    /// 直接为普客订单创建并写入酒水。
    /// </summary>
    /// <remarks>
    /// 普客酒水不经过送餐盘，成功写入订单字段后同步扣减库存，避免 UI 显示与游戏库存产生偏差。
    /// </remarks>
    private static (bool Ok, string Message) TryDeliverNormalOrderBeverage(object order, int beverageId, string beverageName)
    {
        var currentQuantity = GetBeverageQuantity(beverageId);
        if (currentQuantity == 0)
        {
            return (false, $"{beverageName} 当前库存为 0，无法送达普客订单。");
        }

        var sellable = InvokeStatic(DataBaseCoreTypeName, "AsNewBeverage", new object?[] { beverageId });
        if (sellable == null)
        {
            return (false, $"无法从游戏数据库创建酒水对象：{beverageName} #{beverageId}。");
        }

        if (!TrySetNormalOrderServedBeverage(order, sellable))
        {
            return (false, $"无法把酒水 {beverageName} 写入普客订单。");
        }

        if (currentQuantity > 0)
        {
            InvokeRuntimeStorageOut("BeverageOut", beverageId);
        }

        var quantityText = currentQuantity < 0 ? "无限库存" : $"剩余 {Math.Max(0, currentQuantity - 1)}";
        return (true, $"{beverageName} 已送达普客订单（{quantityText}）。");
    }

    /// <summary>
    /// 从普客保温缓存取出目标料理并写入订单。
    /// </summary>
    /// <remarks>
    /// 优先按自动收菜时记录的对象键匹配，避免多个同名料理存在时误送；缺少对象键时才退回同料理 ID 匹配。
    /// 如果订单写入失败，会尽力将料理放回保温缓存。
    /// </remarks>
    private static (bool Ok, string Message) TryDeliverNormalOrderFoodFromStorage(
        object order,
        string orderKey,
        int deskCode,
        int expectedFoodId,
        string foodName)
    {
        var configure = GetSingletonInstance(IzakayaConfigureTypeName);
        if (configure == null)
        {
            return (false, "当前料理暂存容器不可用。");
        }

        var storedFoods = ReadStoredFoodList(configure);
        if (storedFoods == null)
        {
            return (false, "未读取到普客保温箱料理列表。");
        }

        var preferredStoredFoodKey = FindCompletedNormalStoredFoodKey(orderKey, deskCode, expectedFoodId);
        var food = FindStoredNormalFood(storedFoods, expectedFoodId, preferredStoredFoodKey, out var matchedByKey);
        if (food == null)
        {
            return (false, $"普客保温箱中没有找到目标料理 {foodName}（料理 #{expectedFoodId}）。");
        }

        if (!TryRemoveStoredNormalFood(configure, storedFoods, food, out var removeMessage))
        {
            return (false, $"已找到 {foodName}，但无法从普客保温箱取出：{removeMessage}");
        }

        if (!TrySetNormalOrderServedFood(order, food))
        {
            TryRestoreStoredNormalFood(configure, food);
            return (false, $"已从保温箱取出 {foodName}，但无法写入普客订单；已尝试放回保温箱。");
        }

        ForgetCompletedNormalOrderCooking(orderKey, deskCode, expectedFoodId);
        var matchText = matchedByKey ? "目标订单回执" : "同名料理";
        return (true, $"{foodName} 已从普客保温箱送达订单（来源：{matchText}）。");
    }

    private static object? ReadOrderServedFood(object order)
    {
        return ReadMember(order, "ServFood")
            ?? TryInvokeInstanceValue(order, "get_ServFood");
    }

    private static object? ReadOrderServedBeverage(object order)
    {
        return ReadMember(order, "ServBeverage")
            ?? TryInvokeInstanceValue(order, "get_ServBeverage");
    }

    /// <summary>
    /// 将料理写入普客订单的已送达字段。
    /// </summary>
    /// <remarks>
    /// 游戏存在“空中待送达”字段和最终字段两层状态。这里先触发待送达字段，再清空并提交最终字段，
    /// 以兼容 UI 动画路径和订单完成判定。
    /// </remarks>
    private static bool TrySetNormalOrderServedFood(object order, object food)
    {
        var visualUpdated = TryInvokeInstance(order, "set_ServedFoodInAir", new object?[] { food })
            || WriteMember(order, "ServedFoodInAir", food);
        TryInvokeInstance(order, "set_ServedFoodInAir", new object?[] { null });
        WriteMember(order, "ServedFoodInAir", null);
        var committed = TryInvokeInstance(order, "set_ServFood", new object?[] { food })
            || WriteMember(order, "ServFood", food);
        return committed || (visualUpdated && ReadMember(order, "ServFood") != null);
    }

    private static bool TrySetNormalOrderServedBeverage(object order, object beverage)
    {
        var visualUpdated = TryInvokeInstance(order, "set_ServedBeverageInAir", new object?[] { beverage })
            || WriteMember(order, "ServedBeverageInAir", beverage);
        TryInvokeInstance(order, "set_ServedBeverageInAir", new object?[] { null });
        WriteMember(order, "ServedBeverageInAir", null);
        var committed = TryInvokeInstance(order, "set_ServBeverage", new object?[] { beverage })
            || WriteMember(order, "ServBeverage", beverage);
        return committed || (visualUpdated && ReadMember(order, "ServBeverage") != null);
    }

    /// <summary>
    /// 记录普客料理已经被自动收至保温缓存。
    /// </summary>
    /// <remarks>
    /// 该记录是本地自动化回执，不替代游戏实际容器。后续每轮会重新验证暂存容器中是否仍存在目标料理。
    /// </remarks>
    private static void RememberCompletedNormalOrderCooking(CookingCollectionTarget target, object storedFood)
    {
        if (target.Kind != CookingCollectionTargetKind.NormalOrder || target.FoodId < 0) return;

        lock (PendingCookingLock)
        {
            var now = DateTime.UtcNow;
            PruneCompletedNormalOrderCooking(now);
            CompletedNormalCookingCollections.RemoveAll(item => IsSameCompletedNormalOrderCooking(item, target.OrderKey, target.DeskCode, target.FoodId));
            CompletedNormalCookingCollections.Add(new CompletedNormalCookingCollection
            {
                OrderKey = target.OrderKey,
                DeskCode = target.DeskCode,
                FoodId = target.FoodId,
                FoodName = target.FoodName,
                StoredFoodKey = GetStoredFoodKey(storedFood),
                StoredAtUtc = now,
                LastConfirmedAtUtc = now,
            });
        }
    }

    /// <summary>
    /// 确认普客目标料理是否仍存在于保温缓存中。
    /// </summary>
    /// <remarks>
    /// 如果运行时容器可验证且目标已消失，会撤销本地回执，允许下一轮重新制作或重新送达。
    /// </remarks>
    private static bool TryConfirmCompletedNormalOrderCooking(string orderKey, int deskCode, int foodId, out string message)
    {
        lock (PendingCookingLock)
        {
            PruneCompletedNormalOrderCooking(DateTime.UtcNow);
            var completed = CompletedNormalCookingCollections.FirstOrDefault(item => IsSameCompletedNormalOrderCooking(item, orderKey, deskCode, foodId));
            if (completed != null)
            {
                var storageStatus = ReadNormalStorageStatus(foodId, completed.StoredFoodKey);
                if (storageStatus.HasTarget)
                {
                    completed.LastConfirmedAtUtc = DateTime.UtcNow;
                    message = $"目标料理 {completed.FoodName} 已在普客保温箱中，等待玩家手动送达。{storageStatus.Message}";
                    return true;
                }

                if (storageStatus.CanVerify)
                {
                    CompletedNormalCookingCollections.Remove(completed);
                    message = $"此前记录 {completed.FoodName} 已收至普客保温箱，但当前暂存容器未读取到该料理，已撤销本地回执并重新处理。{storageStatus.Message}";
                    AppendAutomationLog("completed-missing", CookingCollectionTarget.ForNormalOrder(null, null, null, orderKey, foodId, completed.FoodName, deskCode, ""), message);
                    return false;
                }

                message = $"目标料理 {completed.FoodName} 已有收取回执，但当前暂存容器暂时不可验证，下一轮继续复查。{storageStatus.Message}";
                return true;
            }
        }

        message = "";
        return false;
    }

    private static bool IsSameCompletedNormalOrderCooking(CompletedNormalCookingCollection item, string orderKey, int deskCode, int foodId)
    {
        if (item.FoodId != foodId) return false;
        if (!string.IsNullOrWhiteSpace(orderKey) && !string.IsNullOrWhiteSpace(item.OrderKey))
        {
            return string.Equals(orderKey, item.OrderKey, StringComparison.Ordinal);
        }

        return item.DeskCode >= 0 && deskCode >= 0 && item.DeskCode == deskCode;
    }

    private static string FindCompletedNormalStoredFoodKey(string orderKey, int deskCode, int foodId)
    {
        lock (PendingCookingLock)
        {
            PruneCompletedNormalOrderCooking(DateTime.UtcNow);
            return CompletedNormalCookingCollections
                .FirstOrDefault(item => IsSameCompletedNormalOrderCooking(item, orderKey, deskCode, foodId))
                ?.StoredFoodKey ?? "";
        }
    }

    /// <summary>
    /// 读取供前端展示的普客保温缓存状态。
    /// </summary>
    /// <remarks>
    /// 返回值同时区分“存在自动化回执”和“仅发现同名料理”，让 UI 可以提示当前料理是否可靠绑定到目标订单。
    /// </remarks>
    internal static NormalStoredFoodSnapshot ReadNormalOrderStoredFoodSnapshot(string orderKey, int deskCode, int foodId)
    {
        if (foodId < 0)
        {
            return new NormalStoredFoodSnapshot(false, false, 0, "订单没有有效料理 ID。");
        }

        lock (PendingCookingLock)
        {
            PruneCompletedNormalOrderCooking(DateTime.UtcNow);
            var completed = CompletedNormalCookingCollections.FirstOrDefault(item => IsSameCompletedNormalOrderCooking(item, orderKey, deskCode, foodId));
            if (completed != null)
            {
                var storageStatus = ReadNormalStorageStatus(foodId, completed.StoredFoodKey);
                var count = CountNormalStoredFoods(foodId);
                if (storageStatus.HasTarget)
                {
                    completed.LastConfirmedAtUtc = DateTime.UtcNow;
                    return new NormalStoredFoodSnapshot(true, true, Math.Max(1, count), storageStatus.Message);
                }

                if (storageStatus.CanVerify)
                {
                    CompletedNormalCookingCollections.Remove(completed);
                    var message = $"此前记录 {completed.FoodName} 已收至普客保温箱，但当前暂存容器未读取到该料理，已撤销本地回执。{storageStatus.Message}";
                    AppendAutomationLog("completed-missing", CookingCollectionTarget.ForNormalOrder(null, null, null, orderKey, foodId, completed.FoodName, deskCode, ""), message);
                    return new NormalStoredFoodSnapshot(count > 0, false, Math.Max(0, count), message);
                }

                return new NormalStoredFoodSnapshot(true, true, Math.Max(1, count), storageStatus.Message);
            }
        }

        var fallbackCount = CountNormalStoredFoods(foodId);
        return fallbackCount > 0
            ? new NormalStoredFoodSnapshot(true, false, fallbackCount, $"普客保温箱中存在同名料理 {fallbackCount} 份，但没有匹配到该订单的自动化回执。")
            : new NormalStoredFoodSnapshot(false, false, 0, "普客保温箱中没有读取到目标料理。");
    }

    private static void ForgetCompletedNormalOrderCooking(string orderKey, int deskCode, int foodId)
    {
        lock (PendingCookingLock)
        {
            CompletedNormalCookingCollections.RemoveAll(item => IsSameCompletedNormalOrderCooking(item, orderKey, deskCode, foodId));
        }
    }

    private static void PruneCompletedNormalOrderCooking(DateTime now)
    {
        CompletedNormalCookingCollections.RemoveAll(item => now - item.StoredAtUtc >= CompletedNormalCookingRememberTimeout);
    }

    /// <summary>
    /// 验证游戏暂存容器中是否存在目标料理。
    /// </summary>
    /// <remarks>
    /// 对象键命中最可靠；若对象键丢失但同料理 ID 存在，则仍视为可用，但提示中保留匹配精度差异。
    /// </remarks>
    private static NormalStorageStatus ReadNormalStorageStatus(int foodId, string storedFoodKey)
    {
        var configure = GetSingletonInstance(IzakayaConfigureTypeName);
        if (configure == null)
        {
            return NormalStorageStatus.Unknown("当前料理暂存容器不可用。");
        }

        var storedFoods = ReadStoredFoodList(configure);
        if (storedFoods == null)
        {
            return NormalStorageStatus.Unknown("未读取到 StoredFoods 列表。");
        }

        var rawCount = ToInt(TryInvokeInstanceValue(storedFoods, "get_Count")
            ?? ReadMember(storedFoods, "Count")
            ?? ReadMember(storedFoods, "_size"), -1);
        var scanned = 0;
        var matchedById = 0;
        var matchedByObject = false;
        foreach (var food in ReadObjectEnumerable(storedFoods))
        {
            scanned++;
            if (!string.IsNullOrWhiteSpace(storedFoodKey) && string.Equals(GetStoredFoodKey(food), storedFoodKey, StringComparison.Ordinal))
            {
                matchedByObject = true;
            }

            if (IsSellable(food, sellableType: 0, id: foodId))
            {
                matchedById++;
            }
        }

        if (matchedByObject || matchedById > 0)
        {
            var detail = matchedByObject
                ? $"已确认目标对象仍在暂存容器中（同料理数量 {matchedById}）。"
                : $"已确认暂存容器中存在同名料理 {matchedById} 份。";
            return NormalStorageStatus.Verified(true, detail);
        }

        if (rawCount > 0 && scanned == 0)
        {
            return NormalStorageStatus.Unknown($"暂存容器显示有 {rawCount} 个对象，但当前无法枚举。");
        }

        return NormalStorageStatus.Verified(false, rawCount >= 0
            ? $"暂存容器当前总数 {rawCount}，目标料理数量 0。"
            : "暂存容器可读取，但目标料理数量为 0。");
    }

    private static object? ReadStoredFoodList(object configure)
    {
        return ReadMember(configure, "StoredFoods")
            ?? TryInvokeInstanceValue(configure, "get_StoredFoods")
            ?? TryInvokeInstanceValue(configure, "GetStoredFoods");
    }

    private static object? FindStoredNormalFood(object storedFoods, int expectedFoodId, string preferredStoredFoodKey, out bool matchedByKey)
    {
        matchedByKey = false;
        object? fallback = null;
        foreach (var food in ReadObjectEnumerable(storedFoods))
        {
            if (food == null) continue;
            if (!string.IsNullOrWhiteSpace(preferredStoredFoodKey)
                && string.Equals(GetStoredFoodKey(food), preferredStoredFoodKey, StringComparison.Ordinal)
                && IsSellable(food, sellableType: 0, id: expectedFoodId))
            {
                matchedByKey = true;
                return food;
            }

            if (fallback == null && IsSellable(food, sellableType: 0, id: expectedFoodId))
            {
                fallback = food;
            }
        }

        return fallback;
    }

    /// <summary>
    /// 从游戏暂存容器中移除料理对象。
    /// </summary>
    /// <remarks>
    /// 优先使用原生 RemoveStoredFood；若原生入口返回失败但列表状态已变化，则以验证结果为准。
    /// 只有确认无法移除时，才尝试托管列表、IL2CPP Remove 或 RemoveAt。
    /// </remarks>
    private static bool TryRemoveStoredNormalFood(object configure, object storedFoods, object food, out string message)
    {
        try
        {
            var beforeCount = GetCollectionCount(storedFoods);
            if (TryInvokeInstance(configure, "RemoveStoredFood", new object?[] { food, -1 })
                || TryInvokeInstance(configure, "RemoveStoredFood", new object?[] { food }))
            {
                var verifyMessage = BuildStoredFoodRemovalVerification(storedFoods, food, beforeCount);
                message = string.IsNullOrWhiteSpace(verifyMessage)
                    ? "已通过原生 RemoveStoredFood 取出。"
                    : $"已通过原生 RemoveStoredFood 取出；{verifyMessage}";
                return true;
            }
            else if (WasStoredFoodRemoved(storedFoods, food, beforeCount, out var nativeFailureVerifyMessage))
            {
                message = string.IsNullOrWhiteSpace(nativeFailureVerifyMessage)
                    ? "原生 RemoveStoredFood 调用未返回成功，但目标料理已不在保温箱列表中。"
                    : $"原生 RemoveStoredFood 调用未返回成功，但目标料理已不在保温箱列表中；{nativeFailureVerifyMessage}";
                return true;
            }

            if (storedFoods is IList managedList)
            {
                var before = managedList.Count;
                managedList.Remove(food);
                if (managedList.Count < before)
                {
                    message = "已从托管列表移除。";
                    return true;
                }
            }

            var removeValue = TryInvokeInstanceValue(storedFoods, "Remove", new object?[] { food });
            if (removeValue is bool removeBool)
            {
                if (removeBool)
                {
                    message = "已从 IL2CPP 列表移除。";
                    return true;
                }
            }
            else if (removeValue != null)
            {
                message = "已调用 Remove。";
                return true;
            }

            var index = FindStoredFoodIndex(storedFoods, food);
            if (index >= 0 && TryInvokeInstance(storedFoods, "RemoveAt", new object?[] { index }))
            {
                message = $"已按索引 {index} 移除。";
                return true;
            }

            message = "未找到可用的 Remove/RemoveAt 入口";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool WasStoredFoodRemoved(object storedFoods, object food, int beforeCount, out string message)
    {
        var currentCount = GetCollectionCount(storedFoods);
        var index = FindStoredFoodIndex(storedFoods, food);
        if (index >= 0)
        {
            message = currentCount >= 0 ? $"当前保温箱数量 {currentCount}" : "";
            return false;
        }

        if (beforeCount >= 0 && currentCount >= 0)
        {
            message = $"保温箱数量 {beforeCount}->{currentCount}";
            return currentCount < beforeCount;
        }
        else
        {
            message = "";
        }

        return beforeCount >= 0 && currentCount == 0;
    }

    private static string BuildStoredFoodRemovalVerification(object storedFoods, object food, int beforeCount)
    {
        return WasStoredFoodRemoved(storedFoods, food, beforeCount, out var message)
            ? message
            : "";
    }

    private static int GetCollectionCount(object collection)
    {
        return ToInt(TryInvokeInstanceValue(collection, "get_Count")
            ?? ReadMember(collection, "Count")
            ?? ReadMember(collection, "_size"), -1);
    }

    private static int FindStoredFoodIndex(object storedFoods, object food)
    {
        var count = ToInt(TryInvokeInstanceValue(storedFoods, "get_Count")
            ?? ReadMember(storedFoods, "Count")
            ?? ReadMember(storedFoods, "_size"), -1);
        if (count <= 0) return -1;

        for (var index = 0; index < Math.Min(count, 256); index++)
        {
            var candidate = TryInvokeInstanceValue(storedFoods, "get_Item", new object?[] { index });
            if (candidate == null) continue;
            if (IsSameObject(candidate, food)) return index;
        }

        return -1;
    }

    private static string GetStoredFoodKey(object? food)
    {
        if (food == null) return "";
        try
        {
            return $"ptr:{ReadObjectPointer(food):x}";
        }
        catch
        {
            return $"hash:{RuntimeHelpers.GetHashCode(food)}";
        }
    }

    /// <summary>
    /// 将完成料理收入普客保温缓存。
    /// </summary>
    /// <remarks>
    /// 自动收菜阶段不直接写入普客订单，先存入保温缓存并建立回执，避免订单尚未稳定时误触发评价。
    /// </remarks>
    private static (bool Remove, string Message) TryCollectNormalOrderFood(PendingCookingCollection pending, object cookedFood)
    {
        if (pending.Target.FoodId >= 0 && !IsSellable(cookedFood, sellableType: 0, id: pending.Target.FoodId))
        {
            return (true, $"{pending.RecipeName} 已完成，但成品不是目标料理 {pending.Target.FoodName}（料理 #{pending.Target.FoodId}），本次不会放入普客保温箱。");
        }

        if (!TryStoreFoodInNormalStorage(cookedFood, pending.Target.FoodId, out var storeMessage))
        {
            return (false, $"{pending.RecipeName} 已完成，但{storeMessage}，等待下一轮重试。");
        }

        RememberCompletedNormalOrderCooking(pending.Target, cookedFood);
        TryResetCookControllerAfterNormalWarmerCollect(pending.CookController, cookedFood);

        return (true, $"{pending.RecipeName} 已自动收至普客保温箱，等待玩家手动送达。{storeMessage}");
    }

    /// <summary>
    /// 调用游戏暂存容器保存料理，并验证保存结果。
    /// </summary>
    private static bool TryStoreFoodInNormalStorage(object cookedFood, int expectedFoodId, out string message)
    {
        try
        {
            var configure = GetSingletonInstance(IzakayaConfigureTypeName);
            if (configure == null)
            {
                message = "当前料理暂存容器不可用";
                return false;
            }

            var beforeCount = CountStoredFoods(configure, expectedFoodId);
            if (!TryInvokeStoreFood(configure, cookedFood))
            {
                message = "写入料理暂存容器失败：未找到可用的 StoreFood 入口";
                return false;
            }

            var storageStatus = ReadNormalStorageStatus(expectedFoodId, GetStoredFoodKey(cookedFood));
            var afterCount = CountStoredFoods(configure, expectedFoodId);
            if (beforeCount >= 0 && afterCount >= 0 && afterCount <= beforeCount)
            {
                message = $"写入料理暂存容器后数量未增加（料理 #{expectedFoodId}: {beforeCount}->{afterCount}）";
                return false;
            }

            if (!storageStatus.HasTarget && storageStatus.CanVerify)
            {
                message = $"写入料理暂存容器后未读取到目标料理（料理 #{expectedFoodId}）。{storageStatus.Message}";
                return false;
            }

            message = beforeCount >= 0 && afterCount >= 0
                ? $"料理暂存数量 {beforeCount}->{afterCount}。"
                : storageStatus.Message;
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入料理暂存容器失败：{ex.GetBaseException().Message}";
            return false;
        }
    }

    private static bool TryInvokeStoreFood(object configure, object cookedFood)
    {
        return TryInvokeInstance(configure, "StoreFood", new object?[] { cookedFood, -1 })
            || TryInvokeInstance(configure, "StoreFood", new object?[] { cookedFood });
    }

    private static void TryRestoreStoredNormalFood(object configure, object food)
    {
        if (TryInvokeStoreFood(configure, food)) return;

        try
        {
            var storedFoods = ReadStoredFoodList(configure);
            if (storedFoods is IList managedList)
            {
                managedList.Add(food);
                return;
            }

            TryInvokeInstance(storedFoods!, "Add", new object?[] { food });
        }
        catch
        {
            // 这里只做尽力回滚，调用方会报告原始送达失败；回滚失败不能掩盖主错误。
        }
    }

    private static int CountStoredFoods(object configure, int expectedFoodId)
    {
        if (expectedFoodId < 0) return -1;

        var storedFoods = ReadStoredFoodList(configure);
        if (storedFoods == null) return -1;

        var rawCount = ToInt(TryInvokeInstanceValue(storedFoods, "get_Count")
            ?? ReadMember(storedFoods, "Count")
            ?? ReadMember(storedFoods, "_size"), -1);
        var count = 0;
        var scanned = 0;
        foreach (var food in ReadObjectEnumerable(storedFoods))
        {
            scanned++;
            if (IsSellable(food, sellableType: 0, id: expectedFoodId))
            {
                count++;
            }
        }

        if (scanned == 0 && rawCount > 0)
        {
            return -1;
        }

        return count;
    }

    private static int CountNormalStoredFoods(int expectedFoodId)
    {
        if (expectedFoodId < 0) return 0;

        try
        {
            var configure = GetSingletonInstance(IzakayaConfigureTypeName);
            if (configure == null) return 0;
            var count = CountStoredFoods(configure, expectedFoodId);
            return Math.Max(0, count);
        }
        catch
        {
            return 0;
        }
    }

    private static void TryResetCookControllerAfterNormalWarmerCollect(object cookController, object cookedFood)
    {
        try
        {
            TryInvokeInstance(cookController, "CloseCookingVisual", Array.Empty<object?>());
            TryClearCookController(cookController, cookedFood);
        }
        catch
        {
            // 不调用 AfterPlayerExtract：该路径代表玩家手动收菜，可能触发超过“放入保温箱”的厨具或订单副作用。
        }
    }
}
