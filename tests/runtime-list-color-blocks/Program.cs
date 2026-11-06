using MystiaStewardCompanion.Save;
using UnityEngine;
using UnityEngine.UI;
using EngineObject = UnityEngine.Object;

// 直接执行生产绑定、裁剪与回写实现；只替换Unity/IL2CPP边界，不将GPU或Harmony注入记为通过。
var assertions = 0;
void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
void Near(float expected, float actual, string message) => Check(MathF.Abs(expected - actual) < .0001f, message);
RuntimeUiTargetSnapshot Target(int slot, RuntimeTargetHighlightColor color) => new(
    slot < 4 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal, color, true, false, false, false, false,
    "order-" + slot, slot < 4 ? "" : "ptr:" + (slot + 1).ToString("x"),
    1, slot, 34, new[] { 11 }, Array.Empty<int>(), 16, 2, "bands-test", slot);
RuntimeTargetHighlightPalette Palette(params RuntimeTargetHighlightColor[] colors) =>
    new(colors.Select((color, slot) => Target(slot, color)).ToArray());
(Selectable Button, Image Source, Image Icon, ClusterProbe Cluster) Fixture()
{
    var owner = new GameObject("native row");
    var source = owner.Add(new Image { color = new Color(.4f, .5f, .6f, .35f), sprite = new Sprite() });
    var button = owner.Add(new Selectable { image = source });
    var iconOwner = new GameObject("native icon"); iconOwner.transform.SetParent(owner.transform, false);
    var icon = iconOwner.Add(new Image { sprite = new Sprite() });
    var cluster = owner.Add(new ClusterProbe { allElements = new[] { iconOwner.transform } });
    return (button, source, icon, cluster);
}
BackgroundBandVertex Vertex(float x, float y) => new(new(x, y, 0), new(x, y, -1), new(x, y, 2, -1),
    new(255, 255, 255, (byte)(100 + y * 20)), new(x / 4 + .5f, y / 4, 2, 3),
    new(x, y, 4, 5), new(x * 2, y * 2, 6, 7), new(x * 3, y * 3, 8, 9));
var original = new[] { Vertex(0, 0), Vertex(-2, 2), Vertex(0, 4), Vertex(2, 2) };
var indices = new[] { 0, 1, 2, 0, 2, 3 };
VertexHelper Mesh()
{
    var result = new VertexHelper();
    new RuntimePinnedListMeshAccess(result).Write(original, indices);
    return result;
}
void SameNative(VertexHelper mesh)
{
    var vertices = new List<BackgroundBandVertex>(); var found = new List<int>();
    new RuntimePinnedListMeshAccess(mesh).Read(vertices, found);
    Check(vertices.SequenceEqual(original) && found.SequenceEqual(indices), "原网格未完整保留或恢复。");
}
var palette = Palette(new(255, 219, 46), new(95, 172, 211), new(249, 115, 115), new(124, 203, 138),
    new(186, 156, 232), new(255, 255, 255), new(0, 0, 0), new(240, 140, 120));
var row = Fixture(); var nativeColor = row.Source.color; var iconColor = row.Icon.color;
var nativeSprite = row.Source.sprite; var children = row.Source.transform.Children.ToArray();
var created = EngineObject.Created;
var helper = RuntimePinnedListColorBlocks.TryCreate(row.Button, row.Source, row.Cluster, row.Icon.sprite, out var failure);
Check(helper != null && failure == "", "精确背景绑定失败。");
Check(EngineObject.Created == created && children.SequenceEqual(row.Source.transform.Children), "绑定新增了独立背景或改变原生层级。");

for (var count = 1; count <= 8; count++)
{
    var claims = (RuntimeUiTargetKinds)((1 << count) - 1);
    Check(helper!.Apply(claims, palette), "目标颜色更新失败。");
    var dirty = row.Source.DirtyCount;
    var mesh = Mesh();
    Check(helper.ApplyToNativeMesh(mesh, claims, palette, out failure), "原网格分色失败：" + failure);
    Check(row.Source.DirtyCount == dirty, "网格生成回调又标脏，可能触发重建循环。");
    var area = 0f;
    var visible = new HashSet<Color32>();
    for (var index = 0; index < mesh.m_Indices.Count; index += 3)
    {
        var a = mesh.m_Positions[mesh.m_Indices[index]];
        var b = mesh.m_Positions[mesh.m_Indices[index + 1]];
        var c = mesh.m_Positions[mesh.m_Indices[index + 2]];
        area += MathF.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) / 2;
        var band = Math.Clamp((int)(((a.x + b.x + c.x) / 3 + 2) / 4 * count), 0, count - 1);
        var expected = palette[band].ToUnityColor();
        for (var corner = 0; corner < 3; corner++)
        {
            var slot = mesh.m_Indices[index + corner]; var position = mesh.m_Positions[slot]; var color = mesh.m_Colors[slot];
            Check(MathF.Abs(position.x) + MathF.Abs(position.y - 2) <= 2.00001f, "色带伸出原生菱形网格。");
            // 按实际输出三角形的 X 位置判断颜色，能直接发现误用旧的上下分带方向。
            Check(position.x >= -2 + 4f * band / count - .00001f
                && position.x <= -2 + 4f * (band + 1) / count + .00001f, "三角形跨越左右等宽颜色边界。");
            Near(expected.r * 255, color.r, "颜色发生跨带渐变。");
            Near(expected.g * 255, color.g, "目标绿色通道未保留。");
            Near(expected.b * 255, color.b, "目标蓝色通道未保留。");
            Check(MathF.Abs(color.a - (100 + position.y * 20)) <= 1, "原顶点alpha未插值或被重复相乘。");
            Near(position.x / 4 + .5f, mesh.m_Uv0S[slot].x, "图集UV0横坐标被破坏。");
            Near(position.y / 4, mesh.m_Uv0S[slot].y, "图集UV0纵坐标被破坏。");
            Near(position.y, mesh.m_Uv1S[slot].y, "UV1未保留。");
            Near(position.y * 2, mesh.m_Uv2S[slot].y, "UV2未保留。");
            Near(position.y * 3, mesh.m_Uv3S[slot].y, "UV3未保留。");
            Near(position.x, mesh.m_Normals[slot].x, "法线未插值。");
            Near(position.y, mesh.m_Tangents[slot].y, "切线未插值。");
            visible.Add(new Color32(color.r, color.g, color.b, 255));
        }
    }
    Near(8, area, "分色改变了原背景总面积或重复覆盖。");
    Check(visible.Count == count, "没有同时显示全部颜色。");
    Check(row.Source.color == nativeColor && row.Icon.color == iconColor && ReferenceEquals(row.Source.sprite, nativeSprite), "原属性或图标被改写。");
    Check(helper.RestoreNativeMesh(mesh, out failure), "撤回时恢复失败：" + failure); SameNative(mesh);
}

// 颜色槽可以回收，但空间排列必须跟随订单生命周期。直接检查生产回写的三角形，
// 而非调用同一收集函数生成期望值，以捕获“槽位/颜色未变就不标脏”的回归。
var chronologicalColors = new[] { new RuntimeTargetHighlightColor(95, 172, 211), new RuntimeTargetHighlightColor(255, 219, 46),
    new RuntimeTargetHighlightColor(249, 115, 115), new RuntimeTargetHighlightColor(124, 203, 138) };
RuntimeUiTargetSnapshot ChronologicalTarget(int slot, long lifecycle, RuntimeTargetHighlightColor color,
    int recipe = 34, int beverage = 16, int ingredient = 11) => new(
        slot < 4 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal, color,
        true, false, true, false, false, (slot < 4 ? "R-" : "N-") + lifecycle.ToString("D4"),
        slot < 4 ? "" : "ptr:" + lifecycle.ToString("x"), lifecycle, slot, recipe,
        new[] { ingredient }, Array.Empty<int>(), beverage, 2, "chronological", slot);
void AssertDisplayOrder(RuntimeUiTargetKinds claims, RuntimeTargetHighlightPalette displayPalette,
    RuntimeTargetHighlightColor[] expected, string message)
{
    var mesh = Mesh();
    Check(helper!.ApplyToNativeMesh(mesh, claims, displayPalette, out var reason), message + ": " + reason);
    var visible = Enumerable.Range(0, mesh.m_Positions.Count)
        .GroupBy(index => new Color32(mesh.m_Colors[index].r, mesh.m_Colors[index].g, mesh.m_Colors[index].b, 255))
        .OrderBy(group => group.Min(index => mesh.m_Positions[index].x)).Select(group => group.Key).ToArray();
    Check(visible.SequenceEqual(expected.Select(color => new Color32(color.R, color.G, color.B, 255))), message);
}
var firstOrders = Enumerable.Range(0, 4).Select(slot => ChronologicalTarget(slot, slot + 1, chronologicalColors[slot])).ToArray();
var firstOrderSet = new RuntimeUiTargetSetSnapshot(1, 1, firstOrders);
var fourClaims = (RuntimeUiTargetKinds)15;
Check(helper!.Apply(fourClaims, firstOrderSet.Palette), "初始四单无法应用");
AssertDisplayOrder(fourClaims, firstOrderSet.Palette, chronologicalColors, "初始四色未按订单先后显示");
var reusedOrders = new[] { firstOrders[3], ChronologicalTarget(0, 5, chronologicalColors[0]), firstOrders[1], firstOrders[2] };
var reusedSet = new RuntimeUiTargetSetSnapshot(2, 1, reusedOrders);
Check(reusedSet.GetRecipeClaims(34) == fourClaims && reusedSet.Palette[0] == firstOrderSet.Palette[0],
    "复用颜色槽时不应改变声明位或其他订单身份");
var beforeReorderDirty = row.Source.DirtyCount;
Check(helper.Apply(fourClaims, reusedSet.Palette), "复用槽位后无法应用颜色");
Check(row.Source.DirtyCount == beforeReorderDirty + 1, "订单更新但槽位与颜色不变时未标记列表网格重建");
var expectedAfterReuse = new[] { chronologicalColors[1], chronologicalColors[2], chronologicalColors[3], chronologicalColors[0] };
AssertDisplayOrder(fourClaims, reusedSet.Palette, expectedAfterReuse, "新蓝色订单没有排到黄红绿之后");
var repeatDirty = row.Source.DirtyCount;
var reorderedInput = new RuntimeUiTargetSetSnapshot(3, 1, reusedOrders.Reverse());
Check(helper.Apply(fourClaims, reorderedInput.Palette) && row.Source.DirtyCount == repeatDirty,
    "仅输入数组反序便改变显示顺序或重复标脏");
AssertDisplayOrder(fourClaims, reorderedInput.Palette, expectedAfterReuse, "乱序发布改变了既有订单先后");

// 普客和稀客共享生命周期计数；不同实体只过滤自身声明，不能重新按槽位或客人种类排序。
var mixed = new RuntimeUiTargetSetSnapshot(4, 1, new[] {
    ChronologicalTarget(0, 40, chronologicalColors[0], recipe: 34, beverage: 16, ingredient: 11),
    ChronologicalTarget(5, 20, chronologicalColors[1], recipe: 34, beverage: 17, ingredient: 12),
    ChronologicalTarget(1, 10, chronologicalColors[2], recipe: 35, beverage: 17, ingredient: 11),
    ChronologicalTarget(4, 30, chronologicalColors[3], recipe: 35, beverage: 16, ingredient: 12),
});
AssertDisplayOrder(mixed.GetRecipeClaims(34), mixed.Palette, new[] { chronologicalColors[1], chronologicalColors[0] }, "料理子集未按跨客类顺序显示");
AssertDisplayOrder(mixed.GetIngredientClaims(11), mixed.Palette, new[] { chronologicalColors[2], chronologicalColors[0] }, "食材子集未按跨客类顺序显示");
AssertDisplayOrder(mixed.GetBeverageClaims(16), mixed.Palette, new[] { chronologicalColors[3], chronologicalColors[0] }, "酒水子集未按跨客类顺序显示");

// 跨类别同色只合并视觉，合并位置取该资源最早仍在的同色订单；撤回早单后不得保留幽灵位置。
var sharedChronology = new RuntimeUiTargetSetSnapshot(5, 1, new[] {
    ChronologicalTarget(0, 1, chronologicalColors[0]), ChronologicalTarget(1, 2, chronologicalColors[1]),
    ChronologicalTarget(4, 5, chronologicalColors[0]),
});
AssertDisplayOrder((RuntimeUiTargetKinds)19, sharedChronology.Palette, new[] { chronologicalColors[0], chronologicalColors[1] }, "跨类同色未在最早声明位置合并");
AssertDisplayOrder((RuntimeUiTargetKinds)18, sharedChronology.Palette, new[] { chronologicalColors[1], chronologicalColors[0] }, "撤回较早同色声明后位置未跟随剩余订单");

// 颜色声明与生产目标独立保留，同色仅合并视觉；无变化Tick不会请求重新生成或分配。
var sameColor = new RuntimeTargetHighlightColor(10, 20, 30);
var shared = new RuntimeUiTargetSetSnapshot(1, 1, new[] { Target(0, sameColor), Target(4, sameColor) });
Check(shared.GetRecipeClaims(34) == (RuntimeUiTargetKinds)17, "同色独立订单声明丢失。");
var sharedMesh = Mesh();
Check(helper!.ApplyToNativeMesh(sharedMesh, shared.GetRecipeClaims(34), shared.Palette, out failure), "同色合并失败。");
Check(sharedMesh.m_Colors.All(value => value.r == 10 && value.g == 20 && value.b == 30), "同色被重复叠加。");
helper.Apply((RuntimeUiTargetKinds)255, palette);
for (var index = 0; index < 1000; index++) helper.Apply((RuntimeUiTargetKinds)255, palette);
var before = GC.GetAllocatedBytesForCurrentThread(); var dirtyBefore = row.Source.DirtyCount;
for (var index = 0; index < 10000; index++) helper.Apply((RuntimeUiTargetKinds)255, palette);
var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Check(allocated == 0 && row.Source.DirtyCount == dirtyBefore, "无变化帧分配内存或重建网格。");
Check(EngineObject.Created == created, "分色创建了额外Unity对象。");

// 输入校验在Clear之前完成；写入中一次性引擎异常也应恢复本次原网格。
var failedMesh = Mesh(); failedMesh.FailNextVertex = true;
Check(!helper.ApplyToNativeMesh(failedMesh, RuntimeUiTargetKinds.Rare, palette, out failure), "写入异常没有报告。");
SameNative(failedMesh);
var empty = new VertexHelper();
Check(helper.ApplyToNativeMesh(empty, RuntimeUiTargetKinds.Rare, palette, out failure)
    && empty.m_Positions.Count == 0 && helper.RefreshVisibility(), "合法空网格错误地撤销了背景租约。");
var afterEmpty = Mesh();
Check(helper.ApplyToNativeMesh(afterEmpty, RuntimeUiTargetKinds.Rare, palette, out failure), "空网格后无法继续正常染色。");
var invalid = Mesh(); invalid.m_Uv3S.RemoveAt(0); var originalPositions = invalid.m_Positions.ToArray();
Check(!helper.ApplyToNativeMesh(invalid, RuntimeUiTargetKinds.Rare, palette, out failure), "通道缺失仍写入。");
Check(invalid.m_Positions.SequenceEqual(originalPositions) && invalid.m_Indices.SequenceEqual(indices), "输入拒绝后原数据被清空。");
var output = new List<BackgroundBandVertex>();
// 左右分区同时覆盖九宫格中孔：每个格子的 UV 区间不同，分色不得补实中孔或将 UV 拉成整块矩形。
var sliced = new List<BackgroundBandVertex>(); var slicedIndices = new List<int>();
var xs = new[] { -2f, -1f, 1f, 2f }; var ys = new[] { 0f, .5f, 3.5f, 4f };
var us = new[] { 0f, .1f, .9f, 1f }; var vs = new[] { 0f, .05f, .95f, 1f };
for (var yi = 0; yi < 4; yi++)
    for (var xi = 0; xi < 4; xi++) sliced.Add(Vertex(xs[xi], ys[yi]) with { Uv0 = new(us[xi], vs[yi], 0, 0) });
for (var yi = 0; yi < 3; yi++)
    for (var xi = 0; xi < 3; xi++)
    {
        if (xi == 1 && yi == 1) continue;
        var a = yi * 4 + xi;
        slicedIndices.AddRange(new[] { a, a + 1, a + 5, a, a + 5, a + 4 });
    }
Check(RuntimeBackgroundBandMesh.TryBuild(sliced, slicedIndices, Enumerable.Range(0, 8).Select(i => palette[i]).ToArray(), output, out failure, BackgroundBandDirection.LeftToRight),
    "九宫格分色失败。");
float Remap(float value, float[] positions, float[] texture)
{
    var section = value <= positions[1] ? 0 : value <= positions[2] ? 1 : 2;
    return texture[section] + (value - positions[section]) / (positions[section + 1] - positions[section]) * (texture[section + 1] - texture[section]);
}
var slicedArea = 0f;
for (var i = 0; i < output.Count; i += 3)
{
    var a = output[i].Position; var b = output[i + 1].Position; var c = output[i + 2].Position;
    slicedArea += MathF.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) / 2;
    var center = new Vector3((a.x + b.x + c.x) / 3, (a.y + b.y + c.y) / 3, 0);
    Check(!(MathF.Abs(center.x) < 1 && center.y > .5f && center.y < 3.5f), "原生中孔被独立色块填实。");
}
Near(10, slicedArea, "九宫格分色面积改变。");
foreach (var vertex in output)
{
    Near(Remap(vertex.Position.x, xs, us), vertex.Uv0.x, "九宫格横向图集坐标被拉伸。");
    Near(Remap(vertex.Position.y, ys, vs), vertex.Uv0.y, "九宫格纵向图集坐标被拉伸。");
}
var attenuated = original.Select(vertex => vertex with { Color = new Color32(128, 64, 32, 90) }).ToArray();
Check(RuntimeBackgroundBandMesh.TryBuild(attenuated, indices, new[] { new RuntimeTargetHighlightColor(128, 255, 128) }, output, out failure, BackgroundBandDirection.LeftToRight)
    && output.All(vertex => vertex.Color == new Color32(64, 64, 16, 90)), "原生顶点色调制或alpha被丢弃。");
var withUnused = original.Concat(new[] { Vertex(100, 2) }).ToArray();
Check(RuntimeBackgroundBandMesh.TryBuild(withUnused, indices, new[] { sameColor }, output, out failure, BackgroundBandDirection.LeftToRight)
    && output.All(vertex => vertex.Position.x >= -2 && vertex.Position.x <= 2), "未使用顶点影响了实际背景分区。");
Check(!RuntimeBackgroundBandMesh.TryBuild(original, new[] { 0, 1, 99 }, new[] { sameColor }, output, out failure, BackgroundBandDirection.LeftToRight) && output.Count == 0,
    "无效索引未拒绝。");
var nan = original.ToArray(); nan[0] = nan[0] with { Position = new(float.NaN, 0, 0) };
Check(!RuntimeBackgroundBandMesh.TryBuild(nan, indices, new[] { sameColor }, output, out failure, BackgroundBandDirection.LeftToRight), "非有限坐标未拒绝。");
var many = Enumerable.Range(0, 15000).SelectMany(_ => indices).ToArray();
Check(!RuntimeBackgroundBandMesh.TryBuild(original, many, Enumerable.Range(0, 8).Select(i => palette[i]).ToArray(), output, out failure, BackgroundBandDirection.LeftToRight)
    && output.Count == 0, "复杂网格没有有界拒绝。");

row.Source.enabled = false; Check(helper.RefreshVisibility(), "仅关闭显示错误地失效了数据身份。");
row.Source.enabled = true; row.Source.color = new Color(.2f, .3f, .4f, .18f);
Check(helper.RefreshVisibility() && row.Source.color.a == .18f, "原生透明度被回写。");
row.Icon.sprite = new Sprite(); Check(!helper.RefreshVisibility(), "图标池复用未撤销旧租约。");
var lastDirty = row.Source.DirtyCount; helper.Dispose(); helper.Dispose();
Check(row.Source.DirtyCount == lastDirty + 1 && !row.Source.Destroyed && !row.Icon.Destroyed, "退休未请求恢复或破坏原生资源。");

var bad = Fixture();
Check(RuntimePinnedListColorBlocks.TryCreate(bad.Button, bad.Source, bad.Cluster, new Sprite(), out failure) == null, "未知图标未拒绝。");
bad.Cluster.allElements = new[] { bad.Icon.transform, bad.Icon.transform };
Check(RuntimePinnedListColorBlocks.TryCreate(bad.Button, bad.Source, bad.Cluster, bad.Icon.sprite, out failure) == null, "歧义图标未拒绝。");
bad.Cluster.allElements = new[] { bad.Icon.transform }; bad.Icon.transform.SetParent(new GameObject("foreign").transform, false);
Check(RuntimePinnedListColorBlocks.TryCreate(bad.Button, bad.Source, bad.Cluster, bad.Icon.sprite, out failure) == null, "跨背景图标未拒绝。");
Console.WriteLine($"原生列表背景网格专项通过：{assertions} 项断言；无变化10000次Apply分配 {allocated} 字节；未注入Harmony或运行GPU。");
