using MystiaStewardCompanion.Ui;
using UnityEngine;
using UnityEngine.UI;

var count = 0;
void Check(bool condition, string message) { count++; if (!condition) throw new InvalidOperationException(message); }
void Near(double actual, double expected, string message) => Check(Math.Abs(actual - expected) < .001, message);
void Pump(Action tick, Func<bool> done, string message)
{
    // 这里只让后台文件替身完成，绝不在游戏线程等待。真实实现始终逐帧返回而不阻塞读取。
    var stop = DateTime.UtcNow.AddSeconds(5);
    while (!done() && DateTime.UtcNow < stop) { tick(); Thread.Sleep(1); }
    Check(done(), message);
}
byte[] Png(int width = 32, int height = 32)
{
    var png = new byte[45];
    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
    new byte[] { 73, 72, 68, 82 }.CopyTo(png, 12);
    png[16] = (byte)(width >> 24); png[17] = (byte)(width >> 16); png[18] = (byte)(width >> 8); png[19] = (byte)width;
    png[20] = (byte)(height >> 24); png[21] = (byte)(height >> 16); png[22] = (byte)(height >> 8); png[23] = (byte)height;
    return png;
}
IEnumerable<GameObject> Active(string name) => Probe.Objects.Where(item => item.name == name && item.Active);

// 外部归一化配置在小屏到8K之间保持界内与可逆，不再存在任何游戏内拖动命中算法。
var random = new Random(8126);
foreach (var screen in new[] { (1, 1), (180, 90), (640, 360), (1280, 720), (3840, 2160), (7680, 4320) })
for (var i = 0; i < 300; i++)
{
    var layout = new GameFocusOverlayLayout(random.NextDouble() * 3 - 1, random.NextDouble() * 3 - 1, random.NextDouble() * 2, random.NextDouble() * 2);
    var rect = GameFocusOverlayGeometry.Resolve(layout, screen.Item1, screen.Item2);
    Check(rect.X >= 0 && rect.Y >= 0 && rect.Width > 0 && rect.Height > 0, "外部布局产生无效矩形");
    Check(rect.X + rect.Width <= screen.Item1 + .001 && rect.Y + rect.Height <= screen.Item2 + .001, "外部布局超出屏幕");
    var roundtrip = GameFocusOverlayGeometry.Resolve(GameFocusOverlayGeometry.Normalize(rect, screen.Item1, screen.Item2), screen.Item1, screen.Item2);
    Near(roundtrip.X, rect.X, "布局比例往返改变位置"); Near(roundtrip.Width, rect.Width, "布局比例往返改变尺寸");
}
var invalid = new GameFocusOverlayLayout(double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0).Normalize();
Check(double.IsFinite(invalid.X) && invalid.Width > 0 && invalid.Height > 0, "非法比例没有恢复");

Probe.Reset();
var failures = new List<string>();
var reads = 0;
var readingOnMain = 0;
using var runtime = new RuntimeGameFocusOverlay((generation, key) =>
{
    if (Environment.CurrentManagedThreadId == Probe.MainThread) Interlocked.Increment(ref readingOnMain);
    Interlocked.Increment(ref reads); return Png(32, key.StartsWith("rare-guest") ? 64 : 32);
}, failures.Add);
var settings = new GameFocusOverlaySettings { Enabled = true, Layout = new(.1, .1, .5, .8) };
GameFocusOverlayRow Row(string id) => new(id, "1桌 · 灵梦", new[] { "rare-guest:1" },
    new[] { new GameFocusOverlayTag("稀客", GameFocusOverlayTone.Neutral), new GameFocusOverlayTag("待评价", GameFocusOverlayTone.Request) }, "#FF8055", new[] {
    new GameFocusOverlayGroup("点单", new[] { new GameFocusOverlayTag("料理需求：清淡", GameFocusOverlayTone.Request), new GameFocusOverlayTag("酒水需求：低酒精", GameFocusOverlayTone.Request) }),
    new GameFocusOverlayGroup("料理", new[] { new GameFocusOverlayTag("海鲜味噌汤", GameFocusOverlayTone.Recipe, "food:2") }),
    new GameFocusOverlayGroup("配方", new[] { new GameFocusOverlayTag("海苔", GameFocusOverlayTone.Ingredient, "ingredient:3"), new GameFocusOverlayTag("豆腐", GameFocusOverlayTone.Ingredient, "ingredient:4") }),
    new GameFocusOverlayGroup("加料", new[] { new GameFocusOverlayTag("不加料", GameFocusOverlayTone.Extra) }),
    new GameFocusOverlayGroup("厨具", new[] { new GameFocusOverlayTag("煮锅", GameFocusOverlayTone.Cooker, "cooker:5") }),
    new GameFocusOverlayGroup("酒水", new[] { new GameFocusOverlayTag("绿茶", GameFocusOverlayTone.Beverage, "beverage:6") }),
});
var frame = new GameFocusOverlayFrame(true, 10, "media-1", new[] { Row("R-1"), Row("R-2") });
for (var i = 0; i < 10000; i++) runtime.Tick();
Check(Probe.Created == 0 && Probe.InputReads == 0 && reads == 0, "关闭仍创建节点/读取输入或图片");
// 有效经营期间的零行也是有效显示帧。先建立空面板，之后订单到达只切换正文而不重建外壳。
var emptyFrame = frame with { Rows = Array.Empty<GameFocusOverlayRow>() };
runtime.Apply(settings, emptyFrame); runtime.Tick();
Check(Active("Panel").Count() == 1, "有效经营零订单时面板消失");
var retainedPanel = Active("Panel").Single();
var retainedPosition = retainedPanel.Get<RectTransform>().anchoredPosition;
var retainedSize = retainedPanel.Get<RectTransform>().sizeDelta;
Check(Active("EmptyState").Single().Get<Text>().text == "当前无可显示订单" && !Active("OrderCard").Any(), "零订单没有干净的居中空态");
Check(Active("EmptyState").Single().Get<Text>().alignment == TextAnchor.MiddleCenter
    && !Active("EmptyState").Single().Get<Text>().raycastTarget && !retainedPanel.Get<Image>().raycastTarget,
    "空态未居中或阻挡输入");
var emptyWrites = Probe.Writes; var emptyObjects = Probe.Created; var emptyDestroyed = Probe.Destroyed;
var emptyTextures = Probe.TextureCreated; var emptyBytesBefore = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10000; i++) runtime.Tick();
var emptyStableBytes = GC.GetAllocatedBytesForCurrentThread() - emptyBytesBefore;
Check(Probe.Writes == emptyWrites && Probe.Created == emptyObjects && Probe.Destroyed == emptyDestroyed
    && Probe.TextureCreated == emptyTextures && Probe.InputReads == 0 && reads == 0, "稳定空态重复创建/销毁/读图或读取输入");
Check(emptyStableBytes == 0, "稳定空态有托管分配");
runtime.Apply(settings, frame); runtime.Tick();
Pump(runtime.Tick, () => Probe.Decoded == 6, "同键图像未正确合并加载");
Check(ReferenceEquals(retainedPanel, Active("Panel").Single()) && !Active("EmptyState").Any(), "零订单转有订单重建面板或遗留空态");
Check(reads == 6 && readingOnMain == 0, "图像重复读取或主线程执行文件读取");
Check(!Active("Header").Any(), "仍创建面板顶端标题提示");
Check(!Probe.Objects.Any(item => item.Maybe<GraphicRaycaster>() != null), "永久穿透仍创建射线组件");
Check(Active("OrderCard").Count() == 2 && Active("InfoGroup").Count() == 12, "卡片/前缀分组不完整");
Check(Active("TagLabel").Any(item => item.Get<Text>().text == "待评价"), "标题阶段标签缺失");
Check(Active("EntityImage").Count() == 12, "图片未替换头像、料理、配方、厨具和酒水占位");
Check(Probe.Objects.Where(item => item.Active).All(item => item.Maybe<Image>()?.raycastTarget != true
    && item.Maybe<RawImage>()?.raycastTarget != true && item.Maybe<Text>()?.raycastTarget != true), "出现可拦截输入的图形");
Check(Active("TagBorder").First().Get<Image>().sprite!.texture.Pixels![8 * 16 + 8].a == 0, "标签边框内部不透明导致底色叠加");
// 0→有→0→有沿用同一个面板和卡片池，同代图标也不因暂时零订单而重复读取/解码。
var beforeEmptyCycleCreated = Probe.Created; var beforeEmptyCycleDestroyed = Probe.Destroyed;
var beforeEmptyCycleTextures = Probe.TextureCreated;
runtime.Apply(settings, emptyFrame); runtime.Tick();
Check(ReferenceEquals(retainedPanel, Active("Panel").Single()) && Active("EmptyState").Any()
    && !Active("OrderHeading").Any() && !Active("EntityImage").Any(), "空态沿用过期订单正文或销毁面板");
Check(retainedPanel.Get<RectTransform>().anchoredPosition == retainedPosition
    && retainedPanel.Get<RectTransform>().sizeDelta == retainedSize && Active("BorderTop").Any(), "订单清空导致位置/尺寸/边框变化");
runtime.Apply(settings with { Content = settings.Content with { ShowRare = false, ShowNormal = false } }, emptyFrame); runtime.Tick();
Check(ReferenceEquals(retainedPanel, Active("Panel").Single()) && Active("EmptyState").Any(), "过滤后零订单不应隐藏面板");
runtime.Apply(settings, frame with { Rows = new[] { Row("R-new") with { Heading = "2桌 · 魔理沙" } } }); runtime.Tick();
Check(ReferenceEquals(retainedPanel, Active("Panel").Single()) && Active("OrderHeading").Count() == 1
    && Active("OrderHeading").Single().Get<Text>().text.Contains("魔理沙") && !Active("EmptyState").Any(), "再次来单未更新当前事实或恢复了旧订单");
Check(Probe.Created == beforeEmptyCycleCreated && Probe.Destroyed == beforeEmptyCycleDestroyed
    && Probe.TextureCreated == beforeEmptyCycleTextures && Probe.Decoded == 6 && reads == 6,
    "零订单往返重复创建外壳/卡片或读取图标");
runtime.Apply(settings, frame); runtime.Tick();
foreach (var group in Active("InfoGroup"))
{
    var tags = Active("TagBorder").Where(item => item.transform.Parent == group.transform).Select(item => item.Get<RectTransform>()).ToArray();
    for (var i = 0; i < tags.Length; i++)
    {
        Check(tags[i].anchoredPosition.x >= 32 && tags[i].anchoredPosition.x + tags[i].sizeDelta.x <= group.Get<RectTransform>().sizeDelta.x + .01f, "标签越过前缀或分组右边界");
        for (var j = i + 1; j < tags.Length; j++)
            Check(Math.Abs(tags[i].anchoredPosition.y - tags[j].anchoredPosition.y) >= Math.Min(tags[i].sizeDelta.y, tags[j].sizeDelta.y)
                || tags[j].anchoredPosition.x >= tags[i].anchoredPosition.x + tags[i].sizeDelta.x,
                "同分组标签互相覆盖");
    }
}
var heading = Active("OrderHeading").First();
var card = Active("OrderCard").First();
var originalCardY = card.Get<RectTransform>().anchoredPosition.y;
Check(heading.Get<RectTransform>().anchoredPosition.y < 0, "卡片标题布局无效");
var icon = Active("GuestAvatar").First();
Near(icon.Get<RectTransform>().sizeDelta.x, 28, "头像未按Flutter统一尺寸显示");
var writes = Probe.Writes; var objects = Probe.Created; var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < 10000; i++) runtime.Tick();
var stableBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
Check(Probe.Created == objects && Probe.Writes == writes && Probe.InputReads == 0 && reads == 6, "稳定帧仍重建/读图/读输入");
Check(stableBytes == 0, "稳定阅读帧有托管分配");

// 新不可变对象但展示内容完全一致时，不因快照发布重测文字和布局。
runtime.Apply(settings, frame with { Rows = new[] { Row("R-1"), Row("R-2") } }); runtime.Tick();
Check(Probe.Writes == writes, "同内容新快照导致布局重复写入");

// 运行、暂停、按键和鼠标均不改变穿透或布局，完全移除旧编辑门槛。
Input.Down = Input.Up = Input.Held = true; Input.Mouse = new(200, 200, 0); Input.Scroll = new(0, -4);
Time.timeScale = 0; Common.UI.UniversalGameManager.IsInputEnabled = false; runtime.Tick();
Time.timeScale = 1; Common.UI.UniversalGameManager.IsInputEnabled = true; runtime.Tick();
Check(Probe.InputReads == 0 && Probe.Writes == writes, "运行状态或输入改变只读层");
Input.Reset();

// 外部布局、字号、滚动和两类透明度独立生效，图片槽在解码前后保持固定。
settings = settings with { FontSize = 24, Layout = new(.2, .15, .5, .4), ScrollPercent = 100 };
runtime.Apply(settings, frame); runtime.Tick();
Near(Active("GuestAvatar").First().Get<RectTransform>().sizeDelta.x, 42, "头像未跟随字号缩放");
Near(Active("Panel").Single().Get<RectTransform>().anchoredPosition.x, 256, "外部X位置未生效");
Check(Active("OrderCard").First().Get<RectTransform>().anchoredPosition.y > originalCardY, "外部滚动没有到达下方内容");
var textAlpha = Active("TagLabel").First().Get<Text>().color.a;
settings = settings with { BackgroundOpacity = 20 }; runtime.Apply(settings, frame); runtime.Tick();
Near(Active("Panel").Single().Get<Image>().color.a, .2, "背景透明度未生效");
Near(Active("TagLabel").First().Get<Text>().color.a, textAlpha, "背景透明度错误作用文字");
settings = settings with { ContentOpacity = 40, Theme = "dark" }; runtime.Apply(settings, frame); runtime.Tick();
Near(Active("EntityImage").First().Get<RawImage>().color.a, .4, "图像未独立应用内容透明度");
Near(Active("Panel").Single().Get<Image>().color.a, .2, "内容透明度错误作用面板背景");
Near(Active("OrderCard").First().Get<Image>().color.r, 1, "订单颜色映射未生效");
var recipeText = Active("TagLabel").First(item => item.Get<Text>().text == "海鲜味噌汤").Get<Text>().color;
Near(recipeText.r, 1, "暗色料理标签未使用Flutter语义色");

// 长姓名、复制稀客以及未知头像不改变首行高度；最窄显示区同样使用真实字宽省略。
settings = settings with { FontSize = 16, Layout = new(.2, .15, .22, .6), ScrollPercent = 0 };
var longName = Row("R-long") with { Heading = "1桌 · 多人同行的非常长姓名组合👨‍👩‍👧‍👦以及稀客", AvatarKeys = Array.Empty<string>() };
runtime.Apply(settings, frame with { Rows = new[] { longName } }); runtime.Tick();
var firstGroupY = Active("InfoGroup").First().Get<RectTransform>().anchoredPosition.y;
var longHeading = Active("OrderHeading").Single().Get<Text>();
Check(longHeading.text.EndsWith("…") && !longHeading.text.Contains('\n'), "窄卡片长标题没有单行省略");
Check(longHeading.preferredWidth <= Active("OrderHeading").Single().Get<RectTransform>().sizeDelta.x + .01f, "省略后的标题仍超过可用宽度");
Check(Active("GuestAvatar").Count() == 1 && Active("ImagePlaceholder").Any(), "未知头像没有固定占位");
runtime.Apply(settings, frame with { Rows = new[] { longName with { Heading = "1桌 · 灵梦", AvatarKeys = new[] { "rare-guest:1" },
    Badges = new[] { new GameFocusOverlayTag("稀客", GameFocusOverlayTone.Neutral), new GameFocusOverlayTag("已有酒水", GameFocusOverlayTone.Success) } } } }); runtime.Tick();
Near(Active("InfoGroup").First().Get<RectTransform>().anchoredPosition.y, firstGroupY, "标题/状态变化导致正文起点跳动");
settings = settings with { FontSize = 24 };
runtime.Apply(settings, frame with { Rows = new[] { longName } }); runtime.Tick();
var enlargedFirstGroupY = Active("InfoGroup").First().Get<RectTransform>().anchoredPosition.y;
runtime.Apply(settings, frame with { Rows = new[] { longName with {
    Badges = new[] { new GameFocusOverlayTag("稀客", GameFocusOverlayTone.Neutral), new GameFocusOverlayTag("已有酒水", GameFocusOverlayTone.Success) } } } }); runtime.Tick();
Near(Active("InfoGroup").First().Get<RectTransform>().anchoredPosition.y, enlargedFirstGroupY, "150%字号短长状态切换导致正文跳动");
settings = settings with { FontSize = 16, Layout = new(.1, .1, .5, .8) };
runtime.Apply(settings, frame); runtime.Tick();

// 隐藏后相同数据重开，仍应加载本代图片；相同角色不能借用旧资源代的图像。
var textureBeforeEmptyDisable = Active("EntityImage").First().Get<RawImage>().texture!;
runtime.Apply(settings, emptyFrame); runtime.Tick();
runtime.Apply(settings with { Enabled = false }, emptyFrame);
Check(!Active("Panel").Any() && !Active("EmptyState").Any(), "空态关闭功能没有立即隐藏");
Check(textureBeforeEmptyDisable.Destroyed, "空态关闭功能未释放同代图像缓存");
runtime.Apply(settings, frame); runtime.Tick();
Pump(runtime.Tick, () => Probe.Decoded == 12, "相同帧重新启用后图片未恢复");
runtime.Apply(settings, frame with { MediaGeneration = "media-2" }); runtime.Tick();
Check(Active("EntityImage").Count() < 12, "资源换代仍展示上一代图片");
Pump(runtime.Tick, () => Probe.Decoded == 18, "新资源代未重新加载");
runtime.Apply(settings, frame with { MediaGeneration = "media-2", Rows = Array.Empty<GameFocusOverlayRow>() }); runtime.Tick();
Check(Active("EmptyState").Any(), "离场前的零订单面板提前消失");
runtime.Apply(settings, GameFocusOverlayFrame.Hidden);
Check(!Active("Panel").Any() && !Active("EmptyState").Any(), "空态退出经营未隐藏显示层");
runtime.Apply(settings, emptyFrame with { Generation = 0 }); runtime.Tick();
Check(!Active("Panel").Any(), "无有效经营代次仍创建空态面板");

// 字体失败限频恢复；重建后没有面板标题，也必须完整恢复订单正文。
Probe.RejectFont = true; runtime.Apply(settings, frame); runtime.Tick(); runtime.Tick();
Check(failures.Count == 1, "字体失败没有限频隔离");
Probe.RejectFont = false; Time.unscaledTime += 6; runtime.Tick();
Check(Active("OrderHeading").First().Get<Text>().text.Contains("灵梦"), "字体恢复后卡片标题为空");
runtime.Dispose(); var destroyed = Probe.Destroyed; runtime.Dispose(); runtime.Tick();
Check(destroyed == Probe.Destroyed, "销毁非幂等");

// 图像缓存的线程、并发、失效代、解码预算及失败负缓存专项。
var cacheFailures = new List<string>(); var gate = new ManualResetEventSlim(false);
var entered = 0; var concurrent = 0; var maxConcurrent = 0;
using var cache = new RuntimeGameFocusImageCache((generation, key) =>
{
    Interlocked.Increment(ref entered);
    var now = Interlocked.Increment(ref concurrent);
    int previous; do { previous = maxConcurrent; } while (now > previous && Interlocked.CompareExchange(ref maxConcurrent, now, previous) != previous);
    gate.Wait(); Interlocked.Decrement(ref concurrent);
    return key == "food:999" ? new byte[] { 1 } : Png();
}, cacheFailures.Add);
cache.Apply("old", Enumerable.Range(0, 5).Select(id => "food:" + id));
Pump(() => { }, () => entered == 2, "后台并发未开始");
cache.Apply("new", new[] { "food:999", "ingredient:1", "../bad", "ingredient:-1", "ingredient:01" });
Check(entered == 2, "换代绕开并发上限"); gate.Set();
var beforeDecode = Probe.Decoded;
var maximumDecodedPerTick = 0;
Pump(() => { var before = Probe.Decoded; cache.Tick(); maximumDecodedPerTick = Math.Max(maximumDecodedPerTick, Probe.Decoded - before); },
    () => cache.Get("ingredient:1") != null, "新代图片未读取");
Check(maximumDecodedPerTick <= 1, "单帧解码超过1张");
Check(cache.Get("food:0") == null && cache.Get("food:1") == null, "旧代读取完成后污染新代");
Check(maxConcurrent <= 2 && entered == 4, "并发/非法键过滤失效");
Check(Probe.Decoded == beforeDecode + 1 && cacheFailures.Count == 1, "坏PNG被解码或失败未隔离");
var readsBeforeNegative = entered;
cache.Apply("new", new[] { "food:999", "ingredient:1" });
for (var i = 0; i < 1000; i++) cache.Tick();
Check(entered == readsBeforeNegative, "坏图片未做本代负缓存");
var liveTexture = cache.Get("ingredient:1")!; cache.Dispose();
Check(liveTexture.Destroyed, "关闭未释放自有图片纹理");

// 目录中有数百张图片也只保留当前有界集合，不随每轮订单把整本图鉴读入GPU。
var boundedReads = 0;
using var bounded = new RuntimeGameFocusImageCache((generation, key) => { Interlocked.Increment(ref boundedReads); return Png(); }, cacheFailures.Add);
bounded.Apply("bounded", Enumerable.Range(0, 200).Select(id => "food:" + id));
Pump(() => bounded.Tick(), () => bounded.Get("food:127") != null, "有界图片队列未完成");
Check(boundedReads == 128 && bounded.Get("food:128") == null, "超过128张图片缓存上限");
var evictedTexture = bounded.Get("food:0")!;
bounded.Apply("bounded", new[] { "beverage:200" });
Pump(() => bounded.Tick(), () => bounded.Get("beverage:200") != null, "未使用图片没有让出缓存槽位");
Check(evictedTexture.Destroyed && bounded.Get("food:0") == null, "缓存淘汰未销毁自有纹理");
Console.WriteLine($"PASS: game focus mirror {count} assertions; 10000 stable order frames allocate {stableBytes} bytes; 10000 stable empty frames allocate {emptyStableBytes} bytes; managed Unity stubs only, no native/game/GPU execution.");
