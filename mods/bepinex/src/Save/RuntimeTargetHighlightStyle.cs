using UnityEngine;

namespace MystiaStewardCompanion.Save;

internal readonly record struct RuntimeTargetHighlightColor(byte R, byte G, byte B)
{
    public static readonly RuntimeTargetHighlightColor DefaultRare = new(0xFF, 0xDB, 0x2E);
    public static readonly RuntimeTargetHighlightColor DefaultNormal = new(0x5F, 0xAC, 0xD3);

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

internal readonly record struct RuntimeTargetHighlightPalette(
    RuntimeTargetHighlightColor Rare,
    RuntimeTargetHighlightColor Normal);

/// <summary>
/// Computes tint properties from an immutable target palette and pulse clock.
/// A renderer property value does not by itself prove the final GPU-composited color.
/// </summary>
internal static class RuntimeTargetHighlightStyle
{
    private const float PulseFrequency = 5.5f;
    private const float SharedColorRoundTripFrequency = PulseFrequency * 0.5f;
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
        return claims switch
        {
            RuntimeUiTargetKinds.Rare => palette.Rare.ToUnityColor(),
            RuntimeUiTargetKinds.Normal => palette.Normal.ToUnityColor(),
            RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal => BlendTargetColors(
                palette.Rare,
                palette.Normal,
                BuildSharedColorRatio(realtimeSinceStartup)),
            _ => throw new ArgumentOutOfRangeException(nameof(claims), claims, "A highlighted resource must have a target claim."),
        };
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

    private static float BuildSharedColorRatio(float realtimeSinceStartup)
    {
        // Shared-resource hue travels at half the intensity frequency. At both exact color
        // endpoints the intensity pulse is at the same midpoint, so neither order kind is
        // permanently rendered with a stronger tint.
        return (MathF.Sin(realtimeSinceStartup * SharedColorRoundTripFrequency) + 1f) * 0.5f;
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
