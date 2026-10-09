namespace MystiaStewardCompanion.Save;

/// <summary>稀客名单采集时的精确分类；Unknown 必须保留为读取不完整，不能当成普通客人。</summary>
internal enum RuntimeRareGuestKind { Unknown, Normal, Special }

/// <summary>当前 metadata/interop 已核实的控制器集合形状；不接受任意 IEnumerable。</summary>
internal enum RuntimeGuestControllerCollection { PresentedSet, DeskDictionary, QueueList }

/// <summary>
/// 只解析已知控制器中的真实对象类型。集合经常返回 GuestGroupController 基类包装，
/// OrderingGuest 也只返回 GuestBase 包装，因此 CLR 类型名称和数字 ID 都不足以判断稀客。
/// 本类不扫描场景、不从订单推导名单；委托入口仅用于离线验证原生转换边界。
/// </summary>
internal static class RuntimeRareGuestReader
{
    internal const string SpecialControllerType = "NightScene.GuestManagementUtility.SpecialGuestsController";
    internal const string NormalControllerType = "NightScene.GuestManagementUtility.NormalGuestsController";
    internal const string SpecialGuestType = "GameData.Core.Collections.NightSceneUtility.SpecialGuest";

    /// <summary>
    /// 管理器桌号是字典的真实键，可能为 7、28 等稀疏值，不能用 0..Count-1 索引。
    /// 集合缺失、枚举变化和调用失败全部显式报错；调用方保留其他已确认来源并标记不完整。
    /// </summary>
    public static IReadOnlyList<object?> ReadControllers(object? collection, RuntimeGuestControllerCollection kind)
    {
        IReadOnlyList<object?> values;
        RuntimeCollectionReadFailure failure;
        bool success;
        switch (kind)
        {
            case RuntimeGuestControllerCollection.PresentedSet:
                success = RuntimeConcreteCollectionReader.TryReadHashSet(collection, out values, out failure);
                break;
            case RuntimeGuestControllerCollection.DeskDictionary:
                success = RuntimeConcreteCollectionReader.TryReadDictionary(collection, out var entries, out failure);
                // 空字典也必须核对键类型，否则不支持的形状会被误判为已确认无人。
                if (success && (collection!.GetType().GetGenericArguments()[0] != typeof(int)
                    || entries.Any(entry => entry.Key is not int)))
                {
                    success = false;
                    failure = RuntimeCollectionReadFailure.ElementTypeMismatch;
                }
                values = entries.Select(entry => entry.Value).ToArray();
                break;
            case RuntimeGuestControllerCollection.QueueList:
                success = RuntimeConcreteCollectionReader.TryReadList(collection, out values, out failure);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!success) throw new InvalidOperationException($"{kind}: {failure}");
        return values;
    }

    public static RuntimeRareGuestKind Resolve(
        object? controller,
        out object? readableController,
        out object? specialGuest,
        out string reason,
        Func<object?, string, object?>? cast = null,
        Func<object?, string, object?>? read = null)
    {
        readableController = null;
        specialGuest = null;
        reason = "controller missing";
        if (controller == null) return RuntimeRareGuestKind.Unknown;
        cast ??= RuntimeReflectionUtility.TryCastRuntimeObject;
        read ??= RuntimeReflectionUtility.GetMemberValue;
        try
        {
            // 先恢复真实稀客控制器，才能访问只在派生包装上声明的 SpecialGuest 属性。
            var specialController = cast(controller, SpecialControllerType);
            if (specialController != null)
            {
                readableController = specialController;
                specialGuest = cast(read(specialController, "SpecialGuest"), SpecialGuestType);
                reason = specialGuest == null ? "special controller guest unavailable" : "confirmed special controller";
                return specialGuest == null ? RuntimeRareGuestKind.Unknown : RuntimeRareGuestKind.Special;
            }

            // 已核实的普通控制器直接跳过；即便与稀客目录拥有相同数字 ID，也绝不升级为稀客。
            if (cast(controller, NormalControllerType) != null)
            {
                reason = "confirmed normal controller";
                return RuntimeRareGuestKind.Normal;
            }

            // 其他控制器只允许以 OrderingGuest 的真实 SpecialGuest 类型确认，不能按 ID 猜。
            specialGuest = cast(read(controller, "OrderingGuest"), SpecialGuestType);
            if (specialGuest != null)
            {
                readableController = controller;
                reason = "confirmed special ordering guest";
                return RuntimeRareGuestKind.Special;
            }

            reason = "controller type or guest conversion unavailable";
            return RuntimeRareGuestKind.Unknown;
        }
        catch (Exception ex)
        {
            // 生产 TryCast/GetMemberValue 已屏蔽原生反射异常；仍覆盖额外边界失败并清空结果。
            readableController = null;
            specialGuest = null;
            reason = $"guest conversion failed: {ex.GetType().Name}";
            return RuntimeRareGuestKind.Unknown;
        }
    }
}
