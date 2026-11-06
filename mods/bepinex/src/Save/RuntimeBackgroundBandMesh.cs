using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 从原生背景读取的完整顶点纯值。保留四组 UV、法线和切线，避免分色时破坏九宫格、
/// 图集采样、透明边界或后续原生网格效果；此结构不持有任何 Unity 对象。
/// </summary>
internal readonly record struct BackgroundBandVertex(Vector3 Position, Vector3 Normal, Vector4 Tangent,
    Color32 Color, Vector4 Uv0, Vector4 Uv1, Vector4 Uv2, Vector4 Uv3);

/// <summary>调用方明确指定分区方向；当前厨具与物品列表统一左右排列，保留上下算法供几何验证。</summary>
internal enum BackgroundBandDirection { TopToBottom, LeftToRight }

/// <summary>
/// 在原三角形内部沿选定轴的分区边界裁剪，再为各片段染色。颜色边界处复制顶点而不是共享，
/// 因此不会在两种颜色之间产生渐变，也不会生成原始网格之外的矩形覆盖物。
/// </summary>
internal static class RuntimeBackgroundBandMesh
{
    private const int MaximumOutputVertices = 65532;
    // 厨具外缘的暖暗色与游戏桌面协调；只在原三角形内染色，原透明轮廓和 alpha 继续生效。
    private static readonly RuntimeTargetHighlightColor EdgeSeparatorColor = new(45, 32, 30);

    internal static bool TryBuild(IReadOnlyList<BackgroundBandVertex> vertices, IReadOnlyList<int> indices,
        ReadOnlySpan<RuntimeTargetHighlightColor> colors, List<BackgroundBandVertex> output, out string failure,
        BackgroundBandDirection direction = BackgroundBandDirection.TopToBottom, float edgeSeparatorFraction = 0)
    {
        output.Clear();
        failure = "invalid native background mesh";
        if (vertices.Count == 0 || vertices.Count > 65535 || indices.Count == 0 || indices.Count % 3 != 0
            || colors.Length is < 1 or > 8
            || direction is not (BackgroundBandDirection.TopToBottom or BackgroundBandDirection.LeftToRight)
            || !float.IsFinite(edgeSeparatorFraction) || edgeSeparatorFraction is < 0 or > .0625f
            || (edgeSeparatorFraction > 0 && direction != BackgroundBandDirection.LeftToRight)) return false;
        var horizontalAxis = direction == BackgroundBandDirection.LeftToRight;
        var bottom = float.PositiveInfinity;
        var top = float.NegativeInfinity;
        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            if (!Finite(vertex.Position) || !Finite(vertex.Normal) || !Finite(vertex.Tangent)
                || !Finite(vertex.Uv0) || !Finite(vertex.Uv1) || !Finite(vertex.Uv2) || !Finite(vertex.Uv3)) return false;
        }
        // 分区边界只由实际绘制的索引决定；备用但未引用的顶点不能拉伸背景颜色范围。
        for (var index = 0; index < indices.Count; index++)
        {
            var referenced = indices[index];
            if (referenced < 0 || referenced >= vertices.Count) return false;
            var coordinate = Coordinate(vertices[referenced].Position, horizontalAxis);
            bottom = MathF.Min(bottom, coordinate);
            top = MathF.Max(top, coordinate);
        }
        if (!float.IsFinite(top - bottom) || top - bottom <= .000001f) return false;
        var edgeWidth = (top - bottom) * edgeSeparatorFraction;
        var regionCount = colors.Length + (edgeWidth > 0 ? 2 : 0);
        // 单个三角形经两条平行边界裁剪最多五个顶点；八槽为边界重合情况保留余量。
        Span<BackgroundBandVertex> first = stackalloc BackgroundBandVertex[8];
        Span<BackgroundBandVertex> second = stackalloc BackgroundBandVertex[8];
        for (var triangle = 0; triangle < indices.Count; triangle += 3)
        {
            var a = indices[triangle]; var b = indices[triangle + 1]; var c = indices[triangle + 2];
            if (a < 0 || b < 0 || c < 0 || a >= vertices.Count || b >= vertices.Count || c >= vertices.Count)
            { output.Clear(); return false; }
            for (var band = 0; band < regionCount; band++)
            {
                float lower, upper;
                RuntimeTargetHighlightColor color;
                if (band < colors.Length)
                {
                    // 颜色槽位仍按完整背景等分；只有两个外缘让出细线，不改变内部颜色边界。
                    var partition = horizontalAxis ? band : colors.Length - band - 1;
                    lower = MathF.Max(bottom + edgeWidth, bottom + (top - bottom) * partition / colors.Length);
                    upper = MathF.Min(top - edgeWidth, bottom + (top - bottom) * (partition + 1) / colors.Length);
                    color = colors[band];
                }
                else
                {
                    // 两侧分隔线与色区是互不重叠的三角形分区，避免另叠透明矩形造成边缘加深或越界。
                    var leftEdge = band == colors.Length;
                    lower = leftEdge ? bottom : top - edgeWidth;
                    upper = leftEdge ? bottom + edgeWidth : top;
                    color = EdgeSeparatorColor;
                }
                if (upper <= lower) continue;
                first[0] = vertices[a]; first[1] = vertices[b]; first[2] = vertices[c];
                var count = Clip(first[..3], second, lower, keepAbove: true, horizontalAxis);
                count = Clip(second[..count], first, upper, keepAbove: false, horizontalAxis);
                for (var corner = 1; corner + 1 < count; corner++)
                {
                    // 裁剪线恰好经过角点时可能生成零面积片段，去除它们避免边界重复混合。
                    if (Area(first[0].Position, first[corner].Position, first[corner + 1].Position) <= .0000000001f) continue;
                    if (output.Count + 3 > MaximumOutputVertices)
                    { output.Clear(); failure = "native background exceeds bounded color mesh capacity"; return false; }
                    output.Add(Tint(first[0], color));
                    output.Add(Tint(first[corner], color));
                    output.Add(Tint(first[corner + 1], color));
                }
            }
        }
        failure = output.Count == 0 ? "native background has no visible triangles" : "";
        return output.Count > 0;
    }

    private static int Clip(ReadOnlySpan<BackgroundBandVertex> input, Span<BackgroundBandVertex> output,
        float boundary, bool keepAbove, bool horizontalAxis)
    {
        if (input.Length == 0) return 0;
        var count = 0;
        var previous = input[^1];
        var previousCoordinate = Coordinate(previous.Position, horizontalAxis);
        var previousInside = keepAbove ? previousCoordinate >= boundary : previousCoordinate <= boundary;
        foreach (var current in input)
        {
            var currentCoordinate = Coordinate(current.Position, horizontalAxis);
            var currentInside = keepAbove ? currentCoordinate >= boundary : currentCoordinate <= boundary;
            if (currentInside != previousInside)
            {
                var t = (boundary - previousCoordinate) / (currentCoordinate - previousCoordinate);
                output[count++] = Lerp(previous, current, t);
            }
            if (currentInside) output[count++] = current;
            previous = current; previousCoordinate = currentCoordinate; previousInside = currentInside;
        }
        return count;
    }

    private static float Coordinate(Vector3 position, bool horizontalAxis) => horizontalAxis ? position.x : position.y;

    private static BackgroundBandVertex Lerp(BackgroundBandVertex a, BackgroundBandVertex b, float t) => new(
        Lerp(a.Position, b.Position, t), Lerp(a.Normal, b.Normal, t), Lerp(a.Tangent, b.Tangent, t),
        new Color32(LerpByte(a.Color.r, b.Color.r, t), LerpByte(a.Color.g, b.Color.g, t),
            LerpByte(a.Color.b, b.Color.b, t), LerpByte(a.Color.a, b.Color.a, t)),
        Lerp(a.Uv0, b.Uv0, t), Lerp(a.Uv1, b.Uv1, t), Lerp(a.Uv2, b.Uv2, t), Lerp(a.Uv3, b.Uv3, t));

    private static BackgroundBandVertex Tint(BackgroundBandVertex value, RuntimeTargetHighlightColor color)
    {
        // 原生不可用状态的透明度与顶点调制继续生效；图集纹理由原材质按保留的 UV 正常采样。
        var tint = color.ToUnityColor();
        return value with { Color = new Color32((byte)Math.Clamp((int)MathF.Round(value.Color.r * tint.r), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(value.Color.g * tint.g), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(value.Color.b * tint.b), 0, 255), value.Color.a) };
    }

    private static byte LerpByte(byte a, byte b, float t) => (byte)Math.Clamp((int)MathF.Round(a + (b - a) * t), 0, 255);
    private static Vector3 Lerp(Vector3 a, Vector3 b, float t) => new(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
    private static Vector4 Lerp(Vector4 a, Vector4 b, float t) => new(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t,
        a.z + (b.z - a.z) * t, a.w + (b.w - a.w) * t);
    private static float Area(Vector3 a, Vector3 b, Vector3 c) => MathF.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Finite(Vector4 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);
}
