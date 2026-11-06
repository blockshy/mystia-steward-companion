namespace MystiaStewardCompanion.Save;

/// <summary>
/// 厨具摆放背景的颜色归并规则。几何完全由原生图块和共享网格裁剪器决定，
/// 此处只处理纯值声明，不再保留截图校准的固定矩形尺寸。
/// </summary>
internal static class RuntimeCookerColorBlockLayout
{
    /// <summary>
    /// 按调色板缓存的订单生命周期顺序提取可见颜色，与料理、食材及酒水列表一致。
    /// 声明槽和同色合并规则由共享调色板处理；调用者提供栈缓冲区，不产生逐帧分配。
    /// </summary>
    internal static int CollectColors(RuntimeUiTargetKinds claims, RuntimeTargetHighlightPalette palette,
        Span<RuntimeTargetHighlightColor> destination)
        => palette.CollectDisplayColors(claims, destination);
}
