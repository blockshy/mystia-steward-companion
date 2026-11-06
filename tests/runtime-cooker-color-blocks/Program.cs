using MystiaStewardCompanion.Save;
using UnityEngine;
using UnityEngine.Rendering;
using UObject = UnityEngine.Object;

// 执行正式目录解析、原生背景读取和分带绘制；只替换 Unity/游戏边界，不模拟真实 GPU 或输入注入。
var assertions = 0;
const float separatorFraction = .04f;
var separatorColor = new Color32(45, 32, 30, 220);
void Check(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
RuntimeUiTargetSetSnapshot Targets(long generation, int count, bool same = false, long session = 1) => new(generation, session,
    Enumerable.Range(0, count).Select(slot => new RuntimeUiTargetSnapshot(
        slot < 4 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal,
        new RuntimeTargetHighlightColor((byte)(same ? 80 : 20 + slot * 25), 180, 90),
        false, false, true, false, false, (slot < 4 ? "R-" : "N-") + (slot + 1).ToString("D4"),
        slot < 4 ? "" : "ptr:" + (slot + 1).ToString("x"), slot + 1, slot, 1,
        Array.Empty<int>(), Array.Empty<int>(), 1, 1, "test", slot)));
GameObject[] Roots() => UObject.Objects.OfType<GameObject>().Where(value => !value.Destroyed && value.name == "MystiaStewardCompanion.CookerOrderColors").ToArray();
UObject[] Owned() => UObject.Objects.Where(value => !value.Destroyed && value.name.StartsWith("MystiaStewardCompanion.", StringComparison.Ordinal)).ToArray();
Mesh Mesh() => Roots().Single().Components.OfType<MeshFilter>().Single().sharedMesh;
// 本夹具订单色均不同于分隔色。此处只数订单色，边界色在下方单独验证，避免改变既有生命周期断言含义。
int Colors() => Enumerable.Range(0, Mesh().colors32.Length).Select(index => Mesh().colors32[index])
    .Where(value => value != separatorColor).Distinct().Count();
void Refresh(long generation, int count = 8, bool same = false, long session = 1)
{ RuntimeCookerHighlightService.UpdateTargets(Targets(generation, count, same, session)); RuntimeCookerHighlightService.Tick(); }
SortingGroup? GroupOf(Renderer renderer)
{
    for (var current = renderer.transform; current != null; current = current.parent)
        if (current.gameObject.Components.OfType<SortingGroup>().SingleOrDefault() is { enabled: true } group) return group;
    return null;
}

// 通过真实分带网格的重心插值读取 UV，再按 Sprites/Default 的乘色/预乘透明度计算软件样本。
// 本函数不模拟场景遮挡、排序组、色彩空间或 GPU 光栅化，仅验证材质参数与纹理透明孔洞没有丢失。
Color SampleBackground(float x, float y, GameObject? requestedRoot = null)
{
    var sampledRoot = requestedRoot ?? Roots().Single();
    var mesh = sampledRoot.Components.OfType<MeshFilter>().Single().sharedMesh;
    var material = sampledRoot.Components.OfType<MeshRenderer>().Single().sharedMaterial!;
    var texture = (Texture2D)material.mainTexture!;
    for (var index = 0; index < mesh.triangles.Length; index += 3)
    {
        var ia = mesh.triangles[index]; var ib = mesh.triangles[index + 1]; var ic = mesh.triangles[index + 2];
        var a = mesh.vertices[ia]; var b = mesh.vertices[ib]; var c = mesh.vertices[ic];
        var denominator = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
        if (MathF.Abs(denominator) < .000001f) continue;
        var wa = ((b.y - c.y) * (x - c.x) + (c.x - b.x) * (y - c.y)) / denominator;
        var wb = ((c.y - a.y) * (x - c.x) + (a.x - c.x) * (y - c.y)) / denominator;
        var wc = 1 - wa - wb;
        if (wa < -.00001f || wb < -.00001f || wc < -.00001f) continue;
        var ua = mesh.uv[ia]; var ub = mesh.uv[ib]; var uc = mesh.uv[ic];
        var pixel = texture.SampleNearest(ua.x * wa + ub.x * wb + uc.x * wc, ua.y * wa + ub.y * wb + uc.y * wc);
        var ca = mesh.colors32[ia]; var cb = mesh.colors32[ib]; var cc = mesh.colors32[ic];
        var tint = material.GetColor("_Color"); var rendererTint = material.GetColor("_RendererColor");
        var alpha = pixel.a / 255f * (ca.a * wa + cb.a * wb + cc.a * wc) / 255f * tint.a * rendererTint.a;
        return new(pixel.r / 255f * (ca.r * wa + cb.r * wb + cc.r * wc) / 255f * tint.r * rendererTint.r * alpha,
            pixel.g / 255f * (ca.g * wa + cb.g * wb + cc.g * wc) / 255f * tint.g * rendererTint.g * alpha,
            pixel.b / 255f * (ca.b * wa + cb.b * wb + cc.b * wc) / 255f * tint.b * rendererTint.b * alpha, alpha);
    }
    return default;
}

// Work.unity 资产的真实层级：CookAnimator 根 -> CookerLayout(SortingGroup) -> CookerVisual(SpriteRenderer)。
// SpriteRenderer 位于局部(-.5,-.5)，因此不能把根节点当排序组成员，也不能把子节点位置当桌格中心。
var gameRoot = new GameObject("NativeCooker");
var layout = new GameObject("NativeCookerLayout"); layout.transform.SetParent(gameRoot.transform, false);
var group = layout.Add(new SortingGroup { sortingLayerID = -1270080849, sortingOrder = 0 });
var sourceObject = new GameObject("NativeCookerVisual"); sourceObject.transform.SetParent(layout.transform, false);
sourceObject.transform.localPosition = new Vector3(-.5f, -.5f, 0);
var source = sourceObject.Add(new SpriteRenderer { color = new Color(.3f, .4f, .5f, .6f), sortingLayerID = -1270080849, sortingOrder = 0 });
SortingLayer.Values[-1270080849] = 3;
var animator = gameRoot.Add(new NightScene.UI.CookingUtility.CookAnimator { m_CookerSpriteRenderer = source });
gameRoot.transform.position = NightScene.MapManager.Instance.referenceGrid.GetCellCenterWorld(new Vector3Int(2, 4, 0));
var controller = new TestController { visual = animator };
var entry = new RuntimeCookerControllerEntry { Controller = controller, GridPosition = new(2, 4, 0), ControllerIdentity = "ptr:100" };
RuntimeCookerReflection.Entries.Add(entry);
var templateRoot = new GameObject("NativeSelection");
var cluster = templateRoot.Add(new NightScene.Tiles.SelectionCluster());
var regionalObject = new GameObject("NativeRegional"); regionalObject.transform.SetParent(templateRoot.transform, false);
var outlineObject = new GameObject("NativeOutline"); outlineObject.transform.SetParent(templateRoot.transform, false);
var regional = regionalObject.Add(new SpriteRenderer { sharedMaterial = new Material(new Shader { name = "THIZKY/Effects/RegionalHSVFillter" }),
    sortingLayerID = 7, sortingOrder = 15, sprite = new Sprite(), enabled = false });
var outline = outlineObject.Add(new SpriteRenderer { sharedMaterial = new Material(new Shader { name = "THIZKY/Effects/OutlineBlinkOnly" }) });
cluster.Renderers = new[] { regional, outline };
var tiles = NightScene.Tiles.TileManager.Instance;
tiles.onSelection = cluster;
var originalTile = tiles.Tile;
var originalSprite = tiles.Tile.sprite;
var originalTexture = originalSprite.texture;
// 来自离线导出 CookDesk1/colliders_50x_15 的真实颜色样本，不复制游戏图片进测试仓库。
// 棕色(198,134,82,255)应保留可见分色；透明像素(74,28,0,0)即使RGB非零也必须完全透明。
var originalPixels = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(16);
for (var index = 0; index < originalPixels.Length; index++) originalPixels[index] = new Color32(198, 134, 82, 255);
originalPixels[10] = new Color32(74, 28, 0, 0);
originalTexture.wrapMode = TextureWrapMode.Clamp;
originalTexture.SetPixels32(originalPixels); originalTexture.Apply(false, true);
var nativePixelWrites = originalTexture.PixelWrites; var nativeUploads = originalTexture.Uploads;
var firstReadback = Texture2D.ReadPixelsCalls;
RuntimeCookerHighlightService.Resume("test");

// 原图为切角轮廓，测试完整三角形面积/UV插值，而不是拿矩形bounds冒充原背景轮廓。
// 左右每一色都应延伸到最下桌沿，避免顶部横条被锅体挡住后消失。
for (var count = 1; count <= 8; count++)
{
    Refresh(count, count);
    Check(Roots().Length == 1, "未创建精确背景：" + RuntimeCookerHighlightService.Status);
    Check(Colors() == count, "多目标背景没有同时包含全部颜色");
    var mesh = Mesh();
    Check(Enumerable.Range(0, mesh.colors32.Length).Select(index => mesh.colors32[index]).Distinct().Count() == count + 1,
        "原背景未包含唯一的两侧分隔色，或添加了额外颜色");
    var area = 0f;
    for (var index = 0; index < mesh.vertices.Length; index++)
    {
        var point = mesh.vertices[index];
        Check(MathF.Abs(point.x) <= .50001f && MathF.Abs(point.y) <= .50001f
            && MathF.Abs(point.x) + MathF.Abs(point.y) <= .95001f, "分色三角形超出原生切角背景");
        Check(MathF.Abs(mesh.uv[index].x - (point.x + .5f)) < .00001f
            && MathF.Abs(mesh.uv[index].y - (point.y + .5f)) < .00001f, "背景UV被替换或未正确插值");
        Check(mesh.colors32[index].a == 220, "背景未使用220/255覆盖度");
        if (index % 3 == 0)
        {
            var a = mesh.vertices[index]; var b = mesh.vertices[index + 1]; var c = mesh.vertices[index + 2];
            area += MathF.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) / 2;
            Check(mesh.colors32[index] == mesh.colors32[index + 1] && mesh.colors32[index] == mesh.colors32[index + 2],
                "相邻颜色之间仍存在渐变三角形");
            var minX = MathF.Min(a.x, MathF.Min(b.x, c.x)); var maxX = MathF.Max(a.x, MathF.Max(b.x, c.x));
            if (mesh.colors32[index] == separatorColor)
                Check(maxX <= -.5f + separatorFraction + .00001f || minX >= .5f - separatorFraction - .00001f,
                    "分隔色超出两侧4%范围或跨过了订单颜色区");
            else
                Check(minX >= -.5f + separatorFraction - .00001f && maxX <= .5f - separatorFraction + .00001f,
                    "订单颜色区与两侧暗边发生面积重叠");
        }
    }
    Check(MathF.Abs(area - .995f) < .00001f, "背景网格面积发生增加或丢失");
    var leftSeparator = SampleBackground(-.5f + separatorFraction / 2, 0);
    var rightSeparator = SampleBackground(.5f - separatorFraction / 2, 0);
    var separatorAlpha = 220 / 255f;
    Check(MathF.Abs(leftSeparator.r - rightSeparator.r) < .00001f
        && MathF.Abs(leftSeparator.g - rightSeparator.g) < .00001f
        && MathF.Abs(leftSeparator.b - rightSeparator.b) < .00001f
        && MathF.Abs(leftSeparator.a - rightSeparator.a) < .00001f
        && MathF.Abs(leftSeparator.r - separatorColor.r / 255f * separatorAlpha) < .00001f
        && MathF.Abs(leftSeparator.g - separatorColor.g / 255f * separatorAlpha) < .00001f
        && MathF.Abs(leftSeparator.b - separatorColor.b / 255f * separatorAlpha) < .00001f
        && MathF.Abs(leftSeparator.a - separatorAlpha) < .00001f,
        "两侧分隔线不是同一暗色，或改变了原220透明度");
    var boundMask = (Texture2D)Roots().Single().Components.OfType<MeshRenderer>().Single().sharedMaterial!.mainTexture!;
    Check(!ReferenceEquals(boundMask, originalTexture) && boundMask.SampleNearest(.2f, .2f) == new Color32(255, 255, 255, 255)
        && boundMask.SampleNearest(.63f, .63f) == new Color32(255, 255, 255, 0), "遮罩未独立归白或丢失原透明度");
    for (var slot = 0; slot < count; slot++)
    {
        var x = -.5f + (slot + .5f) / count;
        var atBottom = SampleBackground(x, -.49f);
        var expected = Targets(count, count).Palette[slot];
        var expectedAlpha = 220 / 255f;
        Check(MathF.Abs(atBottom.r - expected.R / 255f * expectedAlpha) < .00001f
            && MathF.Abs(atBottom.g - expected.G / 255f * expectedAlpha) < .00001f
            && MathF.Abs(atBottom.b - expected.B / 255f * expectedAlpha) < .00001f
            && MathF.Abs(atBottom.a - expectedAlpha) < .00001f,
            $"{count}色中的第{slot + 1}色没有沿左右分区延伸至下桌沿");
    }
    // 暗边只裁去最外侧的小部分，内部订单色的分界仍按原全宽等分，不能把全部颜色挤压后重新分配。
    for (var boundary = 1; boundary < count; boundary++)
    foreach (var offset in new[] { -.001f, .001f })
    {
        var x = -.5f + boundary / (float)count + offset;
        var sampled = SampleBackground(x, -.3f);
        var expected = Targets(count, count).Palette[offset < 0 ? boundary - 1 : boundary];
        Check(MathF.Abs(sampled.r - expected.R / 255f * separatorAlpha) < .00001f
            && MathF.Abs(sampled.g - expected.G / 255f * separatorAlpha) < .00001f
            && MathF.Abs(sampled.b - expected.B / 255f * separatorAlpha) < .00001f,
            "加入分隔线后内部订单颜色边界不再按原全宽等分");
    }
}
Check(Texture2D.ReadPixelsCalls == firstReadback + 1 && originalTexture.CpuPixelReads == 0,
    "颜色数量改变重新读回图像，或直接读取不可读的原生图集");
var root = Roots().Single();
Check(ReferenceEquals(root.transform.Parent, source.transform), "背景没有挂在精确厨具渲染器下，遗漏真实排序组");
Check(root.transform.position == gameRoot.transform.position, "背景未位于精确桌格中心");
Check(root.transform.localPosition == new Vector3(.5f, .5f, 0), "挂入偏移子节点后桌格中心发生偏移");
var renderer = root.Components.OfType<MeshRenderer>().Single();
Check(ReferenceEquals(GroupOf(renderer), group) && ReferenceEquals(GroupOf(source), group), "背景和锅体没有继承同一排序组");
Check(renderer.sortingLayerID == source.sortingLayerID && renderer.sortingOrder == -1, "背景没有处在锅体的组内前一层");
var material = renderer.sharedMaterial!;
Check(material.HasProperty("_Color") && material.HasProperty("_RendererColor") && material.HasProperty("_Flip"), "材质缺少显式检查的Sprite参数");
Check(material.GetColor("_Color") == Color.white && material.GetColor("_RendererColor") == Color.white
    && material.GetVector("_Flip") == new Vector4(1, 1, 1, 1), "材质乘数会清空网格颜色或翻转位置");
var visibleSample = SampleBackground(-.13f, .13f);
Check(visibleSample.r > 0 && visibleSample.g > 0 && visibleSample.b > 0 && MathF.Abs(visibleSample.a - 220 / 255f) < .00001f,
    "真实棕色桌面纹理经顶点分色后不可见");
Check(SampleBackground(.13f, .13f) == default, "原图透明孔洞被背景色填实");
Check(SampleBackground(.49f, .49f) == default, "软件采样发现原切角轮廓外被绘制");
Check(originalTexture.PixelWrites == nativePixelWrites && originalTexture.Uploads == nativeUploads, "背景绘制改写或上传了原生纹理");
Check(ReferenceEquals(tiles.Tile, originalTile) && ReferenceEquals(tiles.Tile.sprite, originalSprite), "原交互图块身份被更换");
Check(!regional.enabled && !ReferenceEquals(regional.sprite, originalSprite), "错误改写了原生焦点模板");
Check(source.color == new Color(.3f, .4f, .5f, .6f) && source.enabled && source.sharedMaterial == null,
    "厨具本体、蒸汽等原渲染状态被改写");
var created = UObject.Created;
var uploads = Mesh().Uploads;
Time.realtimeSinceStartup = 2;
RuntimeCookerHighlightService.Tick();
Check(UObject.Created == created && Mesh().Uploads == uploads, "不变目录复核重复创建资源或上传网格");
Refresh(10, 2); Refresh(11, 8);
Check(UObject.Created == created && ReferenceEquals(root, Roots().Single()), "颜色数量变化没有复用资源");
Refresh(12, 8, true); Check(Colors() == 1 && Targets(12, 8, true).Targets.Count == 8, "视觉去重破坏订单声明");
Refresh(13, 7, true); Check(Colors() == 1, "删去一笔同色订单错误撤回其他高亮");
RuntimeCookerHighlightService.Tick();
var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
for (var tick = 0; tick < 10000; tick++) RuntimeCookerHighlightService.Tick();
var allocation = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
Check(allocation == 0, $"无变化热帧产生{allocation}字节分配");

// 用正式厨具服务复现“蓝单结束、新蓝单复用原槽”的发布。声明掩码和槽位颜色完全相同，
// 但顺序变化必须上传新网格；仍存活厨具不重建对象或再次读回图像。
var chronologicalColors = new[] { new RuntimeTargetHighlightColor(95, 172, 211), new RuntimeTargetHighlightColor(255, 219, 46),
    new RuntimeTargetHighlightColor(249, 115, 115), new RuntimeTargetHighlightColor(124, 203, 138) };
RuntimeUiTargetSnapshot ChronologicalTarget(int slot, long lifecycle, RuntimeTargetHighlightColor color, int cookerType = 1) => new(
    slot < 4 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal, color,
    false, false, true, false, false, (slot < 4 ? "R-" : "N-") + lifecycle.ToString("D4"),
    slot < 4 ? "" : "ptr:" + lifecycle.ToString("x"), lifecycle, slot, 1,
    Array.Empty<int>(), Array.Empty<int>(), 1, cookerType, "chronological", slot);
void AssertDisplayOrder(RuntimeTargetHighlightColor[] expected, string message)
{
    for (var index = 0; index < expected.Length; index++)
    {
        // 从原网格底沿取样：此处没有透明孔，能按实际X坐标发现旧槽位顺序残留。
        var sample = SampleBackground(-.5f + (index + .5f) / expected.Length, -.3f);
        var color = expected[index]; var alpha = 220 / 255f;
        Check(MathF.Abs(sample.r - color.R / 255f * alpha) < .00001f
            && MathF.Abs(sample.g - color.G / 255f * alpha) < .00001f
            && MathF.Abs(sample.b - color.B / 255f * alpha) < .00001f, message);
    }
}
void PublishChronology(long generation, params RuntimeUiTargetSnapshot[] orders)
{ RuntimeCookerHighlightService.UpdateTargets(new RuntimeUiTargetSetSnapshot(generation, 1, orders)); RuntimeCookerHighlightService.Tick(); }
var firstOrders = Enumerable.Range(0, 4).Select(slot => ChronologicalTarget(slot, slot + 1, chronologicalColors[slot])).ToArray();
PublishChronology(100, firstOrders);
AssertDisplayOrder(chronologicalColors, "初始四色未按订单先后显示");
var chronologyRoot = Roots().Single(); var chronologyCreated = UObject.Created; var chronologyReads = Texture2D.ReadPixelsCalls;
var beforeChronologyUploads = Mesh().Uploads;
var reusedOrders = new[] { firstOrders[3], ChronologicalTarget(0, 5, chronologicalColors[0]), firstOrders[1], firstOrders[2] };
PublishChronology(101, reusedOrders);
Check(Mesh().Uploads == beforeChronologyUploads + 1, "订单先后变化但槽位颜色不变时未重新上传厨具网格");
var expectedAfterReuse = new[] { chronologicalColors[1], chronologicalColors[2], chronologicalColors[3], chronologicalColors[0] };
AssertDisplayOrder(expectedAfterReuse, "厨具新蓝单没有排到黄红绿之后");
Check(ReferenceEquals(chronologyRoot, Roots().Single()) && UObject.Created == chronologyCreated
    && Texture2D.ReadPixelsCalls == chronologyReads, "仅颜色顺序变化便重新创建厨具资源或读取图像");
var repeatedUploads = Mesh().Uploads;
PublishChronology(102, reusedOrders.Reverse().ToArray());
Check(Mesh().Uploads == repeatedUploads, "仅输入列表反序便重复上传厨具网格");
AssertDisplayOrder(expectedAfterReuse, "乱序发布破坏厨具现存订单顺序");

// 普客与稀客由同一生命周期序列排序；只辅助当前锅型的订单参与本锅分色。
PublishChronology(103,
    ChronologicalTarget(0, 40, chronologicalColors[0]), ChronologicalTarget(5, 20, chronologicalColors[1]),
    ChronologicalTarget(1, 10, chronologicalColors[2]), ChronologicalTarget(4, 30, chronologicalColors[3]));
AssertDisplayOrder(new[] { chronologicalColors[2], chronologicalColors[1], chronologicalColors[3], chronologicalColors[0] },
    "跨客类共享厨具被按种类或槽位排序");
PublishChronology(104,
    ChronologicalTarget(0, 40, chronologicalColors[0]), ChronologicalTarget(5, 20, chronologicalColors[1]),
    ChronologicalTarget(1, 10, chronologicalColors[2], cookerType: 2), ChronologicalTarget(4, 30, chronologicalColors[3], cookerType: 2));
AssertDisplayOrder(new[] { chronologicalColors[1], chronologicalColors[0] }, "厨具声明子集没有保留订单先后");

// 同色只合并视觉；最早同色订单退出后，合并色块应按仍在的另一笔订单移动，不得留下旧位置。
PublishChronology(105, ChronologicalTarget(0, 1, chronologicalColors[0]), ChronologicalTarget(1, 2, chronologicalColors[1]),
    ChronologicalTarget(4, 5, chronologicalColors[0]));
AssertDisplayOrder(new[] { chronologicalColors[0], chronologicalColors[1] }, "厨具同色合并未采用最早有效声明");
PublishChronology(106, ChronologicalTarget(1, 2, chronologicalColors[1]), ChronologicalTarget(4, 5, chronologicalColors[0]));
AssertDisplayOrder(new[] { chronologicalColors[1], chronologicalColors[0] }, "较早同色单退出后厨具仍沿用其位置");

// 原模板可有父层；实际世界矩阵和tile平移应生效，不能照搬一个猜测的格宽或居中PPU。
var parent = new GameObject("SelectionParent"); templateRoot.transform.SetParent(parent.transform, false);
templateRoot.transform.position = new Vector3(10, 20, 0);
regionalObject.transform.position = new Vector3(10.2f, 20.3f, 0);
tiles.interactable.CellTransform = Matrix4x4.Translate(new Vector3(.1f, .2f, .3f));
Refresh(14, 4);
Check(root.transform.position == new Vector3(2.6f, 4.7f, .3f), "图块的平移分量没有沿用Native逻辑");
Check(Mesh().vertices[0].x > -.301f, "模板的相对变换被丢弃");
regionalObject.transform.position = templateRoot.transform.position;
tiles.interactable.CellTransform = Matrix4x4.identity;
Refresh(15);

// 非主线程发布与关闭不读取/销毁Unity；回主线程才执行资源释放。
var oldOwned = Owned();
Task.Run(() => { RuntimeCookerHighlightService.UpdateTargets(Targets(16, 2)); RuntimeCookerHighlightService.Tick(); }).GetAwaiter().GetResult();
Check(Colors() == 8, "后台线程修改了Unity背景");
RuntimeCookerHighlightService.Tick(); Check(Colors() == 2, "主线程未处理新目标");
Task.Run(() => RuntimeCookerHighlightService.Suspend("background")).GetAwaiter().GetResult();
Check(!root.Destroyed, "后台线程直接释放Unity对象");
RuntimeCookerHighlightService.Tick();
Check(oldOwned.All(value => value.Destroyed) && Owned().Length == 0, "后台关闭后没有主线程回收");
Check(!originalTexture.Destroyed && !originalSprite.Destroyed && !originalTile.Destroyed, "误释放原生背景资产");
RuntimeCookerHighlightService.Resume("resume"); Refresh(17);
RuntimeCookerHighlightService.BeginTopologyMutation("first"); RuntimeCookerHighlightService.BeginTopologyMutation("nested");
Check(Owned().Length == 0, "拓扑变更未立即释放自有背景");
RuntimeCookerHighlightService.CompleteTopologyMutation("nested"); RuntimeCookerHighlightService.Tick();
Check(Owned().Length == 0, "嵌套拓扑屏障提前释放");
RuntimeCookerHighlightService.CompleteTopologyMutation("outer"); RuntimeCookerHighlightService.Tick();
Check(Colors() == 8, "拓扑变更结束未恢复");
RuntimeCookerReflection.Locks.Add(entry.GridPosition); controller.ThrowOnRead = true;
var reads = RuntimeCookerReflection.StateReads; Refresh(18);
Check(reads == RuntimeCookerReflection.StateReads && Owned().Length == 0, "锁定厨具仍被读取或绘制");
RuntimeCookerReflection.Locks.Clear(); controller.ThrowOnRead = false; Refresh(19);
var damaged = Owned(); UObject.Destroy(Roots().Single()); Time.realtimeSinceStartup = 4; RuntimeCookerHighlightService.Tick();
Check(damaged.All(value => value.Destroyed) && Colors() == 8, "原根提前销毁遗留网格材质或未重新绑定");

// 证据不完整时停止显示，不回退到固定小点或错误图层。
tiles.Tile.type = 4; Refresh(20); Check(Owned().Length == 0, "非CookDesk图块被误绘制"); tiles.Tile.type = 0;
regional.sharedMaterial!.shader.name = "Unknown"; Refresh(21); Check(Owned().Length == 0, "未知模板shader未拒绝");
regional.sharedMaterial!.shader.name = "THIZKY/Effects/RegionalHSVFillter";
cluster.Renderers = new[] { regional, regional }; Refresh(22); Check(Owned().Length == 0, "重复模板背景未拒绝"); cluster.Renderers = new[] { regional, outline };
controller.State.IsEmptyDesk = true; Refresh(23); Check(Owned().Length == 0, "空厨具桌未跳过"); controller.State.IsEmptyDesk = false;
controller.State.CouldOpen = false; Refresh(24); Check(Owned().Length == 0, "未分类不可用厨具仍绘制"); controller.State.CouldOpen = true;
RuntimeCookerReflection.FailLocks = true; Refresh(25); Check(Owned().Length == 0, "锁目录缺失未停止"); RuntimeCookerReflection.FailLocks = false;
source.enabled = false; Refresh(26); Check(Owned().Length == 0 && !source.enabled, "原隐藏厨具被重启"); source.enabled = true;
Shader.Fail = true; Refresh(27); Check(Owned().Length == 0, "创建失败遗留自有资源"); Shader.Fail = false;
Refresh(28); Check(Colors() == 8, "创建失败后无法恢复");
Refresh(29, session: 2); Check(Owned().Length == 0, "其他经营会话的目标未拒绝");
Refresh(30); RuntimeCookerHighlightService.Abandon("scene exit");
Check(Owned().Length == 0 && !source.Destroyed && !originalTexture.Destroyed, "退出场景误销毁原生资源或遗留自有对象");

// Tilemap 的场景排序不能与锅体的组内 order 混比。真实锅体局部 order=0，背景局部 order=-1；
// 场景中的桌面 Tilemap 即使 order 更高，也不能据此拒绝继承原生 SortingGroup 的背景。
RuntimeCookerHighlightService.Resume("sorting test");
var mapRenderer = tiles.interactable.gameObject.Components.OfType<UnityEngine.Tilemaps.TilemapRenderer>().Single();
mapRenderer.sortingLayerID = source.sortingLayerID; mapRenderer.sortingOrder = 20;
Refresh(31); Check(Colors() == 8, "把Tilemap场景排序和组内order混比，误拒绝了有效背景");
var groupedRoot = Roots().Single(); var groupedMesh = Mesh(); var groupedUploads = groupedMesh.Uploads; var groupedCreated = UObject.Created;
Check(ReferenceEquals(GroupOf(groupedRoot.Components.OfType<MeshRenderer>().Single()), group), "重建后遗漏原生排序组");
// Native LayerSortingController 每帧按 offset-(int)(worldY*32) 修改组外排序；Mod 不应跟着重建。
group.sortingOrder = 7 - (int)(layout.transform.position.y * 32);
RuntimeCookerHighlightService.Tick();
Check(ReferenceEquals(GroupOf(groupedRoot.Components.OfType<MeshRenderer>().Single()), group)
    && GroupOf(groupedRoot.Components.OfType<MeshRenderer>().Single())!.sortingOrder == group.sortingOrder,
    "动态组外排序没有由同一祖先继承");
Refresh(32);
Check(ReferenceEquals(groupedRoot, Roots().Single()) && groupedMesh.Uploads == groupedUploads && UObject.Created == groupedCreated,
    "原生排序组变化导致背景对象或网格被反复重建");
Check(RuntimeCookerHighlightService.Status.Contains($"/group:{group.Pointer:x}:{group.sortingLayerID}:{group.sortingOrder}", StringComparison.Ordinal),
    "诊断没有记录精确父排序组身份及当前排序");

// CookAnimator 与控制器身份不变，但其 m_CookerSpriteRenderer 指向新对象时，旧背景必须重绑。
var replacedSource = source;
var replacedOwned = Owned();
var replacementObject = new GameObject("ReplacementCookerVisual"); replacementObject.transform.SetParent(layout.transform, false);
replacementObject.transform.localPosition = new Vector3(-.5f, -.5f, 0);
source = replacementObject.Add(new SpriteRenderer { sortingLayerID = -1270080849, sortingOrder = 0 });
animator.m_CookerSpriteRenderer = source;
Refresh(33);
Check(replacedOwned.All(value => value.Destroyed) && !replacedSource.Destroyed, "替换渲染器时遗留旧背景或误销毁原生锅体");
Check(ReferenceEquals(Roots().Single().transform.Parent, source.transform)
    && Roots().Single().transform.position == gameRoot.transform.position, "同一组件根下替换source未按精确新身份重绑");

var foreignObject = new GameObject("UnrelatedCookerVisual");
var foreignSource = foreignObject.Add(new SpriteRenderer());
animator.m_CookerSpriteRenderer = foreignSource;
Refresh(34); Check(Owned().Length == 0, "非CookAnimator子树的渲染器被错误绑定");
animator.m_CookerSpriteRenderer = source;
source.gameObject.SetActive(false);
Refresh(35); Check(Owned().Length == 0 && !source.gameObject.activeSelf, "非活动source被显示或被Mod擅自启用");
source.gameObject.SetActive(true); layout.SetActive(false);
Refresh(36); Check(Owned().Length == 0 && !layout.activeSelf, "父排序组节点隐藏时仍绑定背景");
layout.SetActive(true);
var nativeCenter = gameRoot.transform.position;
gameRoot.transform.position = new Vector3(nativeCenter.x + .25f, nativeCenter.y, nativeCenter.z);
Refresh(37); Check(Owned().Length == 0 && RuntimeCookerHighlightService.Status.Contains("grid anchor mismatch"),
    "修正source父链后丢失了CookAnimator根与精确桌格中心的验证");
gameRoot.transform.position = nativeCenter;
Refresh(38); Check(Colors() == 8, "恢复有效父链后背景无法重建");

// 仅局部 order 已到下界的这一锅无法安全插入背景；不能因此清空其他锅的有效辅助。
source.sortingOrder = -32768;
Refresh(39); Check(Owned().Length == 0, "组内order下溢仍然创建背景");
var secondRoot = new GameObject("SecondCooker");
secondRoot.transform.position = NightScene.MapManager.Instance.referenceGrid.GetCellCenterWorld(new Vector3Int(3, 4, 0));
var secondLayout = new GameObject("SecondCookerLayout"); secondLayout.transform.SetParent(secondRoot.transform, false);
var secondGroup = secondLayout.Add(new SortingGroup { sortingLayerID = -1270080849, sortingOrder = -144 });
var secondSourceObject = new GameObject("SecondCookerVisual"); secondSourceObject.transform.SetParent(secondLayout.transform, false);
secondSourceObject.transform.localPosition = new Vector3(-.5f, -.5f, 0);
var secondSource = secondSourceObject.Add(new SpriteRenderer { sortingLayerID = -1270080849, sortingOrder = 0 });
var secondAnimator = secondRoot.Add(new NightScene.UI.CookingUtility.CookAnimator { m_CookerSpriteRenderer = secondSource });
RuntimeCookerReflection.Entries.Add(new RuntimeCookerControllerEntry { Controller = new TestController { visual = secondAnimator }, GridPosition = new(3, 4, 0), ControllerIdentity = "ptr:200" });
Refresh(40); Check(Roots().Length == 1 && ReferenceEquals(Roots().Single().transform.Parent, secondSource.transform)
    && ReferenceEquals(GroupOf(Roots().Single().Components.OfType<MeshRenderer>().Single()), secondGroup), "单锅排序失败清空其他正常厨具");
var readsBeforeSecondCooker = Texture2D.ReadPixelsCalls;
source.sortingOrder = 0; Refresh(41); Check(Roots().Length == 2, "单锅排序恢复后未重新接入");
var sharedTextures = Roots().Select(value => value.Components.OfType<MeshRenderer>().Single().sharedMaterial!.mainTexture).ToArray();
Check(ReferenceEquals(sharedTextures[0], sharedTextures[1]) && Texture2D.ReadPixelsCalls == readsBeforeSecondCooker,
    "多个厨具使用同一图块时没有共享遮罩或重复读回");
Check(originalTexture.PixelWrites == nativePixelWrites && originalTexture.Uploads == nativeUploads
    && originalTexture.SampleNearest(.63f, .63f) == new Color32(74, 28, 0, 0), "生命周期或排序更新改写了原生纹理");
var finalSceneOwned = Owned();
RuntimeCookerHighlightService.Abandon("complete");
Check(finalSceneOwned.All(value => value.Destroyed) && Owned().Length == 0 && !originalTexture.Destroyed,
    "场景结束未归还全部遮罩租约，或误销毁原图集");

// 单独执行遮罩管线，覆盖图集小区域、半透明像素、共享引用及错误路径；RT替身只复制像素，不模拟GPU。
var atlas = new Texture2D(8, 8, TextureFormat.RGBA32, false, false) { wrapMode = TextureWrapMode.Clamp };
var atlasPixels = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(64);
for (var index = 0; index < atlasPixels.Length; index++) atlasPixels[index] = new Color32(198, 134, 82, (byte)(index * 4));
atlas.SetPixels32(atlasPixels); atlas.Apply(false, true);
var cropMinimum = new Vector2(.25f, .125f); var cropMaximum = new Vector2(.75f, .625f);
var croppedReads = Texture2D.ReadPixelsCalls;
var lease = RuntimeCookerAlphaMask.Acquire(atlas, cropMinimum, cropMaximum);
var maskTexture = lease.Texture;
Check(maskTexture.width == 4 && maskTexture.height == 4 && !maskTexture.isReadable,
    "遮罩未只读回4×4小区域，或上传后仍保留CPU副本");
Check(lease.MapUv(cropMinimum) == new Vector2(0, 0) && lease.MapUv(cropMaximum) == new Vector2(1, 1),
    "图集裁剪UV端点映射错误");
for (var y = 0; y < 4; y++)
for (var x = 0; x < 4; x++)
{
    var originalUv = new Vector2((x + 2.5f) / 8, (y + 1.5f) / 8);
    var mapped = lease.MapUv(originalUv);
    Check(MathF.Abs(mapped.x - (x + .5f) / 4) < .00001f && MathF.Abs(mapped.y - (y + .5f) / 4) < .00001f,
        "裁剪内部UV没有保持像素中心位置");
    Check(maskTexture.SampleNearest(mapped.x, mapped.y) == new Color32(255, 255, 255, atlasPixels[(y + 1) * 8 + x + 2].a),
        "裁剪遮罩没有逐像素保留原始半透明alpha");
}
var sameCrop = RuntimeCookerAlphaMask.Acquire(atlas, new Vector2(.26f, .14f), new Vector2(.74f, .61f));
Check(ReferenceEquals(sameCrop.Texture, maskTexture) && Texture2D.ReadPixelsCalls == croppedReads + 1,
    "向外取整后相同裁剪没有按原图身份共享读回");
lease.Dispose(); lease.Dispose();
Check(!maskTexture.Destroyed && sameCrop.IsAlive, "释放一个租约提前销毁其他厨具仍使用的遮罩");
sameCrop.Dispose();
Check(maskTexture.Destroyed && !atlas.Destroyed && atlas.CpuPixelReads == 0, "最后租约未回收遮罩或读取了不可读原图");

// 被外部提前销毁的自有遮罩可以重建，迟到的旧租约不能删除同键的新缓存项。
var staleLease = RuntimeCookerAlphaMask.Acquire(atlas, cropMinimum, cropMaximum);
UObject.Destroy(staleLease.Texture);
Check(!staleLease.IsAlive, "遮罩已销毁但租约仍报告存活");
var freshLease = RuntimeCookerAlphaMask.Acquire(atlas, cropMinimum, cropMaximum);
var freshTexture = freshLease.Texture;
staleLease.Dispose();
Check(freshLease.IsAlive && !freshTexture.Destroyed, "旧租约释放误销毁已重建的新遮罩");
freshLease.Dispose();
Check(freshTexture.Destroyed, "重建遮罩未随最后租约释放");

var savedRenderTarget = new RenderTexture(2, 2, 0);
RenderTexture.active = savedRenderTarget;
var stages = new (string Name, Action Inject)[] {
    ("GetTemporary", () => RenderTexture.FailNextGetTemporary = true),
    ("Clear", () => GL.FailNextClear = true),
    ("SetPass", () => Material.FailNextSetPass = true),
    ("LoadPixelMatrix", () => GL.FailNextLoadPixelMatrix = true),
    ("Begin", () => GL.FailNextBegin = true),
    ("Vertex3", () => GL.FailNextVertex = true),
    ("End", () => GL.FailNextEnd = true),
    ("ReadPixels", () => Texture2D.FailNextReadPixels = true),
    ("GetPixels", () => Texture2D.FailNextGetPixels = true),
    ("SetPixels32", () => Texture2D.FailNextSetPixels32 = true),
    ("Apply", () => Texture2D.FailNextApply = true)
};
for (var stage = 0; stage < stages.Length; stage++)
{
    var previousSrgb = stage % 2 == 0;
    GL.sRGBWrite = previousSrgb;
    var beforeObjects = UObject.Created;
    var beforeTargets = RenderTexture.TemporaryAcquisitions - RenderTexture.TemporaryReleases;
    stages[stage].Inject();
    var failed = false;
    var failureDiagnostic = "";
    try { using var unexpectedLease = RuntimeCookerAlphaMask.Acquire(atlas, cropMinimum, cropMaximum); }
    catch (InvalidOperationException exception)
    { failureDiagnostic = exception.Message; failed = exception.Message.Contains("injected " + stages[stage].Name, StringComparison.Ordinal); }
    Check(failed, stages[stage].Name + "失败未透传，可能缓存了半成品");
    var expectedStage = stages[stage].Name switch {
        "GetTemporary" => "render-target", "Clear" => "clear", "SetPass" => "set-pass",
        "LoadPixelMatrix" => "pixel-matrix", "Begin" => "begin", "Vertex3" => "draw", "End" => "end",
        "ReadPixels" => "read-pixels", "GetPixels" => "get-pixels", _ => "mask-upload"
    };
    Check(failureDiagnostic.Contains("failed at " + expectedStage, StringComparison.Ordinal)
        && failureDiagnostic.Contains("crop=2,1,4x4", StringComparison.Ordinal)
        && failureDiagnostic.Contains("source=8x8", StringComparison.Ordinal),
        stages[stage].Name + "失败未留下正确阶段及裁剪诊断");
    Check(ReferenceEquals(RenderTexture.active, savedRenderTarget) && GL.sRGBWrite == previousSrgb
        && GL.MatrixDepth == 0 && GL.BeginDepth == 0,
        stages[stage].Name + "失败没有恢复RT/sRGB或结束GL矩阵/绘制状态");
    Check(RenderTexture.TemporaryAcquisitions - RenderTexture.TemporaryReleases == beforeTargets
        && UObject.Objects.Where(value => (long)value.Pointer > beforeObjects).All(value => value.Destroyed),
        stages[stage].Name + "失败遗留临时RT或遮罩");
    using var recovered = RuntimeCookerAlphaMask.Acquire(atlas, cropMinimum, cropMaximum);
    Check(recovered.IsAlive, stages[stage].Name + "失败后无法重新生成有效遮罩");
}
RenderTexture.active = null; GL.sRGBWrite = false; UObject.Destroy(savedRenderTarget);

// 无效UV、全透明区域和后台调用均应在形成有效缓存前明确失败，不能制造“active但看不见”的假成功。
void CheckMaskFailure(Texture2D sourceTexture, Vector2 minimum, Vector2 maximum, string message)
{
    var rejected = false;
    try { using var unexpectedLease = RuntimeCookerAlphaMask.Acquire(sourceTexture, minimum, maximum); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, message);
}
var beforeInvalidReads = Texture2D.ReadPixelsCalls;
CheckMaskFailure(atlas, new Vector2(float.NaN, 0), new Vector2(1, 1), "非有限UV未拒绝");
CheckMaskFailure(atlas, new Vector2(-.01f, 0), new Vector2(1, 1), "越界UV未拒绝");
CheckMaskFailure(atlas, new Vector2(.5f, .5f), new Vector2(.5f, .5f), "零面积裁剪未拒绝");
Check(Texture2D.ReadPixelsCalls == beforeInvalidReads, "无效UV进入了纹理读回");
var transparentAtlas = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
CheckMaskFailure(transparentAtlas, new Vector2(0, 0), new Vector2(1, 1), "全透明遮罩仍被报告可用");
Check(Owned().Length == 0, "失败的全透明遮罩被永久缓存");
var beforeBackground = UObject.Created;
var backgroundRejected = Task.Run(() => {
    try { using var unexpectedLease = RuntimeCookerAlphaMask.Acquire(atlas, cropMinimum, cropMaximum); return false; }
    catch (InvalidOperationException) { return true; }
}).GetAwaiter().GetResult();
Check(backgroundRejected && UObject.Created == beforeBackground, "后台Acquire访问或创建了Unity资源");

// 再经正式Blocks验证子图集UV：不能只让Lease.MapUv单测通过，实际上传网格仍错误使用原图UV。
var originalSourceUvs = originalSprite.uv;
var secondEntry = RuntimeCookerReflection.Entries[1]; RuntimeCookerReflection.Entries.RemoveAt(1);
originalSprite.texture = atlas;
originalSprite.uv = originalSourceUvs.Select(value => new Vector2(.25f + value.x * .5f, .125f + value.y * .5f)).ToArray();
RuntimeCookerHighlightService.Resume("atlas integration"); Refresh(42, 4);
var croppedMesh = Mesh();
var uploadedMask = (Texture2D)Roots().Single().Components.OfType<MeshRenderer>().Single().sharedMaterial!.mainTexture!;
Check(uploadedMask.width == 4 && uploadedMask.height == 4 && !ReferenceEquals(uploadedMask, atlas),
    "正式背景未绑定小区域alpha遮罩");
for (var index = 0; index < croppedMesh.vertices.Length; index++)
{
    var point = croppedMesh.vertices[index];
    Check(MathF.Abs(croppedMesh.uv[index].x - (point.x + .5f)) < .00001f
        && MathF.Abs(croppedMesh.uv[index].y - (point.y + .5f)) < .00001f,
        "正式背景上传了原图集UV，导致裁剪遮罩错位");
}
var partialAlpha = SampleBackground(-.13f, .13f).a;
Check(MathF.Abs(partialAlpha - atlasPixels[3 * 8 + 3].a / 255f * (220 / 255f)) < .00001f,
    "正式背景裁剪映射后丢失半透明alpha");
RuntimeCookerHighlightService.Abandon("atlas complete");
originalSprite.texture = originalTexture; originalSprite.uv = originalSourceUvs;
RuntimeCookerReflection.Entries.Add(secondEntry);
Check(atlas.CpuPixelReads == 0, "正式背景直接读取了不可读图集");

// 模拟本次实机故障：所有图形调用成功返回，但GPU读取结果全透明；源纹理本身仍有正常桌面像素。
// 必须经正式Service验证局部降级，不能只测试Acquire抛异常后就认为所有厨具仍可正常辅助。
GL.BlankSourcePointers.Add(originalTexture.Pointer);
var beforeBlankDraws = GL.DrawCalls; var beforeBlankReads = Texture2D.ReadPixelsCalls;
RuntimeCookerHighlightService.Resume("blank GPU service recovery"); Refresh(43);
Check(Roots().Length == 2, "GPU成功但读回空白时清空了全部厨具辅助");
Check(Roots().All(value => ReferenceEquals(value.Components.OfType<MeshRenderer>().Single().sharedMaterial!.mainTexture, originalTexture)),
    "空白遮罩没有降级到精确原图纹理");
Check(GL.DrawCalls == beforeBlankDraws + 1 && Texture2D.ReadPixelsCalls == beforeBlankReads + 1,
    "同一失败图块在多个厨具中重复执行GPU读回");
Check(RuntimeCookerHighlightService.Status.Contains("mode:source-fallback", StringComparison.Ordinal)
    && RuntimeCookerHighlightService.Status.Contains("validate-alpha", StringComparison.Ordinal),
    "原图降级未记录明确模式和失败阶段");
var firstFallback = Roots().Single(value => ReferenceEquals(value.transform.parent, source.transform));
var fallbackSample = SampleBackground(-.13f, .13f, firstFallback);
Check(fallbackSample.r > 0 && fallbackSample.g > 0 && fallbackSample.b > 0
    && MathF.Abs(fallbackSample.a - 220 / 255f) < .00001f, "原图降级仍没有可见采样或丢失220覆盖度");
var fallbackEdge = SampleBackground(-.5f + separatorFraction / 2, 0, firstFallback);
Check(MathF.Abs(fallbackEdge.r - 198 / 255f * (45 / 255f) * (220 / 255f)) < .00001f
    && MathF.Abs(fallbackEdge.g - 134 / 255f * (32 / 255f) * (220 / 255f)) < .00001f
    && MathF.Abs(fallbackEdge.b - 82 / 255f * (30 / 255f) * (220 / 255f)) < .00001f
    && MathF.Abs(fallbackEdge.a - 220 / 255f) < .00001f, "原图降级后丢失分隔色或改变原透明度");
var fallbackMesh = firstFallback.Components.OfType<MeshFilter>().Single().sharedMesh;
for (var index = 0; index < fallbackMesh.vertices.Length; index++)
{
    var point = fallbackMesh.vertices[index];
    Check(MathF.Abs(fallbackMesh.uv[index].x - point.x - .5f) < .00001f
        && MathF.Abs(fallbackMesh.uv[index].y - point.y - .5f) < .00001f,
        "原图降级错误使用裁剪后的UV");
}
GL.BlankSourcePointers.Remove(originalTexture.Pointer);
var cachedFailureReads = Texture2D.ReadPixelsCalls; var cachedFailureDraws = GL.DrawCalls;
Refresh(44, 4);
for (var scan = 0; scan < 4; scan++) { Time.realtimeSinceStartup += 2; RuntimeCookerHighlightService.Tick(); }
for (var tick = 0; tick < 10000; tick++) RuntimeCookerHighlightService.Tick();
Check(Texture2D.ReadPixelsCalls == cachedFailureReads && GL.DrawCalls == cachedFailureDraws
    && ReferenceEquals(firstFallback, Roots().Single(value => ReferenceEquals(value.transform.parent, source.transform))),
    "颜色/目录刷新或10000帧导致失败key重复读回或反复重建背景");

// 另一个图块可正常读取时，仍用增强遮罩；一个失败key不能把正常厨具也降级。
var goodTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false, false) { wrapMode = TextureWrapMode.Clamp };
goodTexture.SetPixels32(originalPixels); goodTexture.Apply(false, true);
var goodTile = new NightScene.Tiles.InteractableTile { sprite = new Sprite { texture = goodTexture } };
tiles.TilesByPosition[new Vector3Int(3, 4, 0)] = goodTile;
Refresh(45, 4);
var goodRoot = Roots().Single(value => ReferenceEquals(value.transform.parent, secondSource.transform));
var goodMask = goodRoot.Components.OfType<MeshRenderer>().Single().sharedMaterial!.mainTexture;
Check(Roots().Length == 2 && !ReferenceEquals(goodMask, goodTexture)
    && ReferenceEquals(firstFallback.Components.OfType<MeshRenderer>().Single().sharedMaterial!.mainTexture, originalTexture)
    && Texture2D.ReadPixelsCalls == cachedFailureReads + 1, "一个读回失败的图块影响了其他正常图块");
tiles.TilesByPosition[new Vector3Int(2, 4, 0)] = goodTile;
Refresh(46, 4);
Check(Roots().All(value => ReferenceEquals(value.Components.OfType<MeshRenderer>().Single().sharedMaterial!.mainTexture, goodMask))
    && Texture2D.ReadPixelsCalls == cachedFailureReads + 1, "同一厨具切换正常图块后未恢复共享遮罩");

// 真正的几何/UV错误仍应拒绝该锅；创建/应用一个候选失败不能走全局ReleaseAll影响其他锅。
var invalidSprite = new Sprite { texture = originalTexture, uv = originalSourceUvs.Select(_ => new Vector2(float.NaN, 0)).ToArray() };
tiles.TilesByPosition[new Vector3Int(2, 4, 0)] = new NightScene.Tiles.InteractableTile { sprite = invalidSprite };
Refresh(47, 4);
Check(Roots().Length == 1 && ReferenceEquals(Roots().Single(), goodRoot)
    && ReferenceEquals(goodRoot.Components.OfType<MeshRenderer>().Single().sharedMaterial!.mainTexture, goodMask),
    "单个候选Apply失败清空或重建了其他正常厨具");
RuntimeCookerHighlightService.Abandon("end failed-readback session");
Check(Owned().Length == 0 && !originalTexture.Destroyed && !goodTexture.Destroyed, "降级场景退出泄漏自有资源或销毁原图");

// 下一经营会话允许重试，之前的纯值负缓存不能永久禁用同一图集。
tiles.TilesByPosition.Clear();
RuntimeNightBusinessLifecycle.Snapshot = new(true, 2, Environment.CurrentManagedThreadId);
var beforeNewSessionReads = Texture2D.ReadPixelsCalls;
RuntimeCookerHighlightService.Resume("next session"); Refresh(48, session: 2);
Check(Roots().Length == 2 && Texture2D.ReadPixelsCalls == beforeNewSessionReads + 1
    && Roots().All(value => !ReferenceEquals(value.Components.OfType<MeshRenderer>().Single().sharedMaterial!.mainTexture, originalTexture)),
    "下一会话未清除失败记录，或没有恢复正常遮罩");
RuntimeCookerHighlightService.Abandon("next session complete");
UObject.Destroy(goodTexture);

// 负缓存满额后不淘汰旧key；新增key直接降级，避免许多坏图块轮流触发昂贵读回。
RuntimeNightBusinessLifecycle.Snapshot = new(true, 3, Environment.CurrentManagedThreadId);
var boundedSources = new List<Texture2D>();
var beforeBoundedDraws = GL.DrawCalls;
for (var index = 0; index < 17; index++)
{
    var badSource = new Texture2D(1, 1, TextureFormat.RGBA32, false, false);
    var singlePixel = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(1);
    singlePixel[0] = new Color32(198, 134, 82, 255);
    badSource.SetPixels32(singlePixel); badSource.Apply(false, true);
    boundedSources.Add(badSource); GL.BlankSourcePointers.Add(badSource.Pointer);
    var acquired = RuntimeCookerAlphaMask.TryAcquireCached(badSource, new Vector2(0, 0), new Vector2(1, 1), out var failedLease, out var reason);
    Check(!acquired && failedLease == null && reason.Length > 0, "失败key或负缓存上限被误记为成功");
}
Check(GL.DrawCalls == beforeBoundedDraws + 16, "负缓存满额后仍执行GPU读回，或未保持16项边界");
var repeatedFailure = RuntimeCookerAlphaMask.TryAcquireCached(boundedSources[0], new Vector2(0, 0), new Vector2(1, 1), out var repeatedLease, out _);
Check(!repeatedFailure && repeatedLease == null && GL.DrawCalls == beforeBoundedDraws + 16, "旧失败key被淘汰并重复读回");
RuntimeNightBusinessLifecycle.Snapshot = new(true, 4, Environment.CurrentManagedThreadId);
GL.BlankSourcePointers.Remove(boundedSources[0].Pointer);
Check(RuntimeCookerAlphaMask.TryAcquireCached(boundedSources[0], new Vector2(0, 0), new Vector2(1, 1), out var recoveredLease, out _)
    && recoveredLease?.IsAlive == true, "换会话后负缓存容量状态没有重置");
recoveredLease!.Dispose();
foreach (var badSource in boundedSources) { GL.BlankSourcePointers.Remove(badSource.Pointer); UObject.Destroy(badSource); }
UObject.Destroy(transparentAtlas); UObject.Destroy(atlas);
Check(RenderTexture.TemporaryAcquisitions == RenderTexture.TemporaryReleases && Owned().Length == 0,
    "完整遮罩回归结束后仍遗留RT或缓存纹理");

// 共享几何器的边界契约：默认不插入暗边，只有显式左右模式可启用；两侧替换原三角区域，不额外叠一层。
List<BackgroundBandVertex> SeparatorInput(float width = 1, float left = -.5f) => originalSprite.vertices
    .Select((point, index) => new BackgroundBandVertex(new Vector3(left + (point.x + .5f) * width, point.y, 0),
        Vector3.back, new Vector4(1, 0, 0, 1), new Color32(255, 255, 255, 220),
        new Vector4(originalSprite.uv[index].x, originalSprite.uv[index].y, 0, 0), Vector4.zero, Vector4.zero, Vector4.zero)).ToList();
float OutputArea(IReadOnlyList<BackgroundBandVertex> output)
{
    var area = 0f;
    for (var index = 0; index < output.Count; index += 3)
    {
        var a = output[index].Position; var b = output[index + 1].Position; var c = output[index + 2].Position;
        area += MathF.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) / 2;
    }
    return area;
}
var separatorInput = SeparatorInput();
var separatorIndices = originalSprite.triangles.Select(value => (int)value).ToArray();
var separatorPalette = Enumerable.Range(0, 8).Select(index => Targets(100, 8).Palette[index]).ToArray();
var separatorOutput = new List<BackgroundBandVertex>();
Check(RuntimeBackgroundBandMesh.TryBuild(separatorInput, separatorIndices, separatorPalette, separatorOutput, out _,
    BackgroundBandDirection.LeftToRight), "未启用分隔线的共享左右分区失败");
Check(separatorOutput.All(value => value.Color != separatorColor)
    && MathF.Abs(OutputArea(separatorOutput) - .995f) < .00001f, "默认参数给其他UI添加了厨具暗边或改变原网格");
foreach (var fraction in new[] { float.NaN, float.PositiveInfinity, -.01f, .0626f })
{
    separatorOutput.Add(separatorInput[0]);
    Check(!RuntimeBackgroundBandMesh.TryBuild(separatorInput, separatorIndices, separatorPalette, separatorOutput, out _,
        BackgroundBandDirection.LeftToRight, fraction) && separatorOutput.Count == 0, "非法分隔比例未拒绝或遗留上次几何");
}
Check(!RuntimeBackgroundBandMesh.TryBuild(separatorInput, separatorIndices, separatorPalette, separatorOutput, out _,
    BackgroundBandDirection.TopToBottom, separatorFraction) && separatorOutput.Count == 0, "上下分区错误接受了仅限左右的分隔线");
Check(RuntimeBackgroundBandMesh.TryBuild(separatorInput, separatorIndices, separatorPalette, separatorOutput, out _,
    BackgroundBandDirection.LeftToRight, .0625f), "合法最大分隔比例被拒绝");
Check(separatorOutput.Select(value => value.Color).Distinct().Count() == 9
    && MathF.Abs(OutputArea(separatorOutput) - .995f) < .00001f, "最大分隔比例吞掉端部订单色或产生重叠/缺口");

// 分隔色必须沿用输入alpha，而非把厨具当前220常量写死进通用几何器。
var varyingAlpha = separatorInput.Select(value => value with {
    Color = new Color32(255, 255, 255, (byte)MathF.Round(64 + (value.Position.y + .5f) * 100))
}).ToList();
Check(RuntimeBackgroundBandMesh.TryBuild(varyingAlpha, separatorIndices, separatorPalette, separatorOutput, out _,
    BackgroundBandDirection.LeftToRight, separatorFraction), "带原生透明度变化的分隔背景失败");
foreach (var vertex in separatorOutput)
    Check(MathF.Abs(vertex.Color.a - (64 + (vertex.Position.y + .5f) * 100)) <= 1.01f,
        "分隔裁剪修改了原生alpha插值");

// 实际CookDesk宽50像素/PPU48，pivot使本格范围[-1/48,49/48]；相邻格平移1。
// 两侧各4%即各2像素，原图本身的2像素交叠使左右暗区重合成约2像素，而非额外画出4像素浮条。
// 这里只验证三角区域位置，不把几何交叠当成真实GPU透明混合通过。
var firstDesk = new List<BackgroundBandVertex>(); var nextDesk = new List<BackgroundBandVertex>();
Check(RuntimeBackgroundBandMesh.TryBuild(SeparatorInput(50 / 48f, -1 / 48f), separatorIndices, separatorPalette, firstDesk, out _,
    BackgroundBandDirection.LeftToRight, separatorFraction), "实际50像素厨具网格分隔失败");
Check(RuntimeBackgroundBandMesh.TryBuild(SeparatorInput(50 / 48f, 1 - 1 / 48f), separatorIndices, separatorPalette, nextDesk, out _,
    BackgroundBandDirection.LeftToRight, separatorFraction), "相邻厨具网格分隔失败");
var firstRightEdge = firstDesk.Where(value => value.Color == separatorColor && value.Position.x > .5f).ToArray();
var nextLeftEdge = nextDesk.Where(value => value.Color == separatorColor && value.Position.x < 1.5f).ToArray();
Check(firstRightEdge.Length > 0 && nextLeftEdge.Length > 0, "相邻厨具接缝缺少暗色区域");
Check(MathF.Abs(firstRightEdge.Min(value => value.Position.x) - nextLeftEdge.Min(value => value.Position.x)) < .00001f
    && MathF.Abs(firstRightEdge.Max(value => value.Position.x) - nextLeftEdge.Max(value => value.Position.x)) < .00001f
    && MathF.Abs(firstRightEdge.Max(value => value.Position.x) - firstRightEdge.Min(value => value.Position.x) - 2 / 48f) < .00001f,
    "相邻厨具的暗边没有与原图2像素交叠区准确重合");
Check(MathF.Abs(OutputArea(firstDesk) - .995f * (50 / 48f)) < .00001f
    && MathF.Abs(OutputArea(nextDesk) - .995f * (50 / 48f)) < .00001f, "实际尺寸分隔改变了原始网格面积");
// 空声明属于正常撤回路径；即使没有构造过调色板，也不能访问其默认空数组而抛出异常。
Span<RuntimeTargetHighlightColor> emptyClaimsBuffer = stackalloc RuntimeTargetHighlightColor[8];
Check(RuntimeCookerColorBlockLayout.CollectColors(RuntimeUiTargetKinds.None, default, emptyClaimsBuffer) == 0,
    "默认调色板的空声明未能正常撤回");
Console.WriteLine($"runtime-cooker-color-blocks: {assertions} assertions passed; 10000 unchanged ticks allocated {allocation} bytes; clipped separators, blank-GPU recovery, bounded retry cache and hierarchy verified; native GPU/game not exercised.");
