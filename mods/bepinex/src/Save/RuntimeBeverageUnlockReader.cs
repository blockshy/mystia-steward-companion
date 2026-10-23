using System.Reflection;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 在已验证的游戏运行态读取链中读取酒水图鉴。精确契约来自当前 metadata/783 interop：
/// RunTimeAlbum.GetAllRecordedBeverageIds() 返回 Il2Cpp List&lt;int&gt;，RVA 0x623070 只读
/// _Beverages；Initialize（0x6252E0）把基础存档与 DLC 图鉴列表写入此字段。
/// GetAllRecordedBeverages 的映射函数 RVA 0x6581A0 调用 DataBaseCore.RefBeverage(id)，
/// 因而这里的整数就是目录 beverage.id，不是数量、索引或配方 ID。
/// </summary>
internal static class RuntimeBeverageUnlockReader
{
    private const string AlbumTypeName = "GameData.RunTime.Common.RunTimeAlbum";

    /// <summary>
    /// 仅在调用方已经确认场景就绪、完成当前存档库存读取之后调用。未初始化列表为 null；
    /// 不主动调用 Initialize、记录方法或存档方法。原生包装只存在于本次同步调用内，
    /// 不跨线程、不跨帧缓存，也不回退到宽松枚举器或字符串转换。
    /// </summary>
    public static bool TryRead(out int[] ids)
    {
        ids = Array.Empty<int>();
        try
        {
            var albumType = RuntimeReflectionUtility.FindType(AlbumTypeName);
            var getter = albumType?.GetMethod(
                "GetAllRecordedBeverageIds",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (getter?.ReturnType != typeof(Il2CppSystem.Collections.Generic.List<int>)) return false;
            if (getter.Invoke(null, null) is not Il2CppSystem.Collections.Generic.List<int> recorded) return false;

            return RuntimeBeverageUnlockProjection.TryRead(
                runtimeReadReady: true,
                () => recorded.Count,
                index => recorded[index],
                out ids);
        }
        catch (Exception)
        {
            // 旧/未知游戏类型、精确 getter 不可用、原生调用失败均只撤销图鉴就绪事实。
            // 禁止把图鉴附加读取的失败升级为库存或自动化读取失败。
            return false;
        }
    }
}
