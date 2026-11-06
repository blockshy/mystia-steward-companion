using UnityEngine;

namespace MystiaStewardCompanion.Save;

internal readonly record struct RuntimeTargetHighlightColor(byte R, byte G, byte B)
{
    // 无显式目标的缺省首色与新配置保持一致；已解析目标始终使用各自保存的槽位颜色。
    public static readonly RuntimeTargetHighlightColor DefaultRare = new(0xF2, 0xCA, 0x5C);
    public static readonly RuntimeTargetHighlightColor DefaultNormal = new(0x5A, 0xA9, 0xE6);

    public static bool TryParseExactHex(string value, out RuntimeTargetHighlightColor color)
    {
        color = default;
        if (value == null || value.Length != 6) return false;

        Span<byte> channels = stackalloc byte[3];
        for (var channel = 0; channel < channels.Length; channel += 1)
        {
            var high = ParseUpperHex(value[channel * 2]);
            var low = ParseUpperHex(value[channel * 2 + 1]);
            if (high < 0 || low < 0) return false;
            channels[channel] = (byte)((high << 4) | low);
        }

        color = new RuntimeTargetHighlightColor(channels[0], channels[1], channels[2]);
        return true;
    }

    public string ToExactHex() => $"{R:X2}{G:X2}{B:X2}";

    internal Color ToUnityColor(float alpha = 1f)
    {
        return new Color(R / 255f, G / 255f, B / 255f, alpha);
    }

    private static int ParseUpperHex(char value)
    {
        if (value is >= '0' and <= '9') return value - '0';
        return value is >= 'A' and <= 'F' ? value - 'A' + 10 : -1;
    }
}

/// <summary>
/// 不可变槽位调色板。声明槽决定订单身份和颜色，显示槽序决定色块左右位置，两者独立；
/// 只在发布时构建顺序，渲染帧按缓存顺序筛选声明，不排序或分配集合。
/// </summary>
internal readonly struct RuntimeTargetHighlightPalette
{
    private readonly RuntimeTargetHighlightColor[] _colors;
    private readonly int[] _displaySlots;
    public RuntimeTargetHighlightPalette(RuntimeTargetHighlightColor rare, RuntimeTargetHighlightColor normal)
    {
        _colors = new RuntimeTargetHighlightColor[8];
        _colors[0] = rare; _colors[1] = normal;
        // 双目标纯值夹具没有生命周期输入，维持原来0、1槽的确定顺序。
        _displaySlots = new[] { 0, 1, 2, 3, 4, 5, 6, 7 };
    }
    public RuntimeTargetHighlightPalette(IReadOnlyList<RuntimeUiTargetSnapshot> targets)
    {
        _colors = new RuntimeTargetHighlightColor[8];
        _displaySlots = new int[8];
        Span<long> lifecycles = stackalloc long[8];
        lifecycles.Fill(long.MaxValue);
        foreach (var target in targets)
        {
            _colors[target.ColorSlot] = target.Color;
            lifecycles[target.ColorSlot] = target.OrderLifecycleSequence;
        }
        // 普客与稀客的序号来自同一个BeginLifecycle计数器，可跨类别比较点单先后。
        // 至多八槽做稳定插入排序；并列生命周期（主要是离线夹具）按槽号保持确定性。
        // 空槽保留在末尾且不参与有效声明，不能把空槽复用当成新订单应回到旧位置的依据。
        for (var slot = 0; slot < _displaySlots.Length; slot++)
        {
            var position = slot;
            while (position > 0 && lifecycles[_displaySlots[position - 1]] > lifecycles[slot])
            {
                _displaySlots[position] = _displaySlots[position - 1];
                position--;
            }
            _displaySlots[position] = slot;
        }
    }
    public RuntimeTargetHighlightColor Rare => this[0];
    public RuntimeTargetHighlightColor Normal => this[1];
    public RuntimeTargetHighlightColor this[int slot] => _colors[slot];

    /// <summary>
    /// 按订单生命周期提取某个资源的可见颜色。先筛选该资源的声明，再合并同色，
    /// 让同色块位于仍关联该资源的最早订单处；撤回一笔声明不会抹除其他同色订单。
    /// 调用方使用八元素栈缓冲区，颜色或排列变化均可通过逐元素比较触发网格更新。
    /// </summary>
    internal int CollectDisplayColors(RuntimeUiTargetKinds claims, Span<RuntimeTargetHighlightColor> destination)
    {
        var mask = (int)claims;
        if (mask < 0 || (mask & ~255) != 0 || destination.Length < 8)
            throw new ArgumentOutOfRangeException(nameof(claims));
        // 空声明不访问颜色存储，保持默认值调色板也能安全撤回显示的既有行为。
        if (mask == 0) return 0;
        var count = 0;
        foreach (var slot in _displaySlots)
        {
            if ((mask & (1 << slot)) == 0) continue;
            var color = _colors[slot];
            var duplicate = false;
            for (var index = 0; index < count; index++) duplicate |= destination[index] == color;
            if (!duplicate) destination[count++] = color;
        }
        return count;
    }
}

/// <summary>
/// Computes tint properties from an immutable target palette and pulse clock.
/// A renderer property value does not by itself prove the final GPU-composited color.
/// </summary>
internal static class RuntimeTargetHighlightStyle
{
    /// <summary>
    /// 订单卡片使用固定透明度，不再随时钟明暗呼吸。多声明的已有颜色轮换仍保留，
    /// 避免共用视觉时只显示第一笔订单；通常每张订单卡只有一个精确声明。
    /// </summary>
    internal static Color BuildOrderHighlightColor(RuntimeUiTargetKinds claims,
        RuntimeTargetHighlightPalette palette, float realtimeSinceStartup)
    {
        var color = ResolveTargetColor(claims, palette, realtimeSinceStartup);
        return new Color(color.r, color.g, color.b, 0.81f);
    }

    /// <summary>桌位同样取消明暗呼吸，保留既有中间亮度与多声明颜色轮换。</summary>
    internal static Color BuildSeatFillColor(RuntimeUiTargetKinds claims,
        RuntimeTargetHighlightPalette palette, float realtimeSinceStartup)
    {
        var color = ResolveTargetColor(claims, palette, realtimeSinceStartup);
        return new Color(color.r, color.g, color.b, 0.575f);
    }

    private const float PulseFrequency = 5.5f;
    // 每色固定停留 1.5 秒、末尾 0.3 秒柔和切换。共享订单越多只延长总周期，不加快闪烁。
    private const float SharedColorDwellSeconds = 1.5f;
    private const float SharedColorTransitionSeconds = 0.3f;
    private const float MinimumOrderHighlightAlpha = 0.62f;
    private const float OrderHighlightAlphaAmplitude = 0.19f;
    private const float MinimumTintBlend = 0.55f;
    private const float TintBlendAmplitude = 0.225f;
    private const float MinimumCookerAlpha = 0.85f;
    private const float MinimumSeatFillAlpha = 0.45f;
    private const float SeatFillAlphaAmplitude = 0.125f;

    internal static Color BuildCookerSpritePulseColor(
        Color originalColor,
        RuntimeUiTargetKinds claims,
        RuntimeTargetHighlightPalette palette,
        float realtimeSinceStartup)
    {
        var targetColor = ResolveTargetColor(claims, palette, realtimeSinceStartup);
        var blend = BuildTintBlend(realtimeSinceStartup);
        return new Color(
            Blend(originalColor.r, targetColor.r, blend),
            Blend(originalColor.g, targetColor.g, blend),
            Blend(originalColor.b, targetColor.b, blend),
            MathF.Max(originalColor.a, MinimumCookerAlpha));
    }

    internal static Color BuildSeatFillPulseColor(
        RuntimeUiTargetKinds claims,
        RuntimeTargetHighlightPalette palette,
        float realtimeSinceStartup)
    {
        var targetColor = ResolveTargetColor(claims, palette, realtimeSinceStartup);
        var alpha = MinimumSeatFillAlpha
            + BuildPulseRatio(realtimeSinceStartup) * SeatFillAlphaAmplitude * 2f;
        return new Color(targetColor.r, targetColor.g, targetColor.b, alpha);
    }

    internal static Color BuildListItemPulseColor(
        Color originalColor,
        RuntimeUiTargetKinds claims,
        RuntimeTargetHighlightPalette palette,
        float realtimeSinceStartup)
    {
        var targetColor = ResolveTargetColor(claims, palette, realtimeSinceStartup);
        var blend = BuildTintBlend(realtimeSinceStartup);
        return new Color(
            Blend(originalColor.r, targetColor.r, blend),
            Blend(originalColor.g, targetColor.g, blend),
            Blend(originalColor.b, targetColor.b, blend),
            originalColor.a);
    }

    internal static Color BuildOrderHighlightPulseColor(
        RuntimeUiTargetKinds claims,
        RuntimeTargetHighlightPalette palette,
        float realtimeSinceStartup)
    {
        var targetColor = ResolveTargetColor(claims, palette, realtimeSinceStartup);
        var alpha = MinimumOrderHighlightAlpha
            + BuildPulseRatio(realtimeSinceStartup) * OrderHighlightAlphaAmplitude * 2f;
        return new Color(targetColor.r, targetColor.g, targetColor.b, alpha);
    }

    private static Color ResolveTargetColor(
        RuntimeUiTargetKinds claims,
        RuntimeTargetHighlightPalette palette,
        float realtimeSinceStartup)
    {
        var mask = (int)claims;
        if (mask <= 0 || (mask & ~255) != 0)
            throw new ArgumentOutOfRangeException(nameof(claims));
        Span<int> slots = stackalloc int[8];
        var count = 0;
        for (var slot = 0; slot < 8; slot++)
            if ((mask & (1 << slot)) != 0) slots[count++] = slot;
        if (count == 1) return palette[slots[0]].ToUnityColor();
        var clock = MathF.Max(0f, realtimeSinceStartup);
        var index = (int)(clock / SharedColorDwellSeconds) % count;
        var within = clock % SharedColorDwellSeconds;
        var amount = Math.Clamp((within - (SharedColorDwellSeconds - SharedColorTransitionSeconds))
            / SharedColorTransitionSeconds, 0f, 1f);
        // 大部分时间精确显示预设色；过渡仅用于消除突变，不把混合色作为第三种订单身份。
        return BlendTargetColors(palette[slots[index]], palette[slots[(index + 1) % count]], amount);
    }

    private static Color BlendTargetColors(
        RuntimeTargetHighlightColor rare,
        RuntimeTargetHighlightColor normal,
        float amount)
    {
        var rareColor = rare.ToUnityColor();
        var normalColor = normal.ToUnityColor();
        return new Color(
            Blend(rareColor.r, normalColor.r, amount),
            Blend(rareColor.g, normalColor.g, amount),
            Blend(rareColor.b, normalColor.b, amount),
            1f);
    }

    private static float BuildPulseRatio(float realtimeSinceStartup)
    {
        return (MathF.Sin(realtimeSinceStartup * PulseFrequency) + 1f) * 0.5f;
    }

    private static float BuildTintBlend(float realtimeSinceStartup)
    {
        return MinimumTintBlend
            + BuildPulseRatio(realtimeSinceStartup) * TintBlendAmplitude * 2f;
    }

    private static float Blend(float original, float target, float amount)
    {
        return original + (target - original) * amount;
    }
}
