using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.Save;
using UnityEngine;

namespace MystiaStewardCompanion.Ui;

/// <summary>
/// 游戏内的Flutter专注卡片展示层。只消费已冻结的身份、文字、颜色及图片键，不计算候选、不读游戏对象。
/// 全部节点为Mod自有，永远不创建GraphicRaycaster、不读Input、不更改暂停/焦点/选中对象。
/// 图像解码、节点布局和销毁均在Unity主线程进行；图像后台读取由独立有界缓存承载。
/// </summary>
internal sealed class RuntimeGameFocusOverlay : IDisposable
{
    private readonly Action<string>? _failure;
    private readonly RuntimeGameFocusImageCache _images;
    private readonly List<CardVisual> _cards = new();
    private readonly List<ImageVisual> _borders = new();
    private GameFocusOverlaySettings _settings = new();
    private GameFocusOverlayFrame _frame = GameFocusOverlayFrame.Hidden;
    private GameFocusOverlayRect _rect;
    private GameObject? _root;
    private ImageVisual? _panel;
    private RectTransform? _viewport;
    private TextVisual? _emptyState;
    private object? _font;
    private Texture2D? _shapeTexture;
    private Sprite? _shape;
    private Texture2D? _outlineTexture;
    private Sprite? _outline;
    private UiMembers? _members;
    private bool _disposed, _layoutDirty, _styleDirty;
    private int _screenWidth, _screenHeight;
    private float _nextRetryAt;
    private string _lastFailure = "";

    public RuntimeGameFocusOverlay(Func<string, string, byte[]?> readImage, Action<string>? onFailure = null)
    {
        _failure = onFailure;
        _images = new(readImage, onFailure);
    }

    /// <summary>只在内容/外观真正改变时置脏；普通快照反复发布同一张卡片不会重新计算标签布局。</summary>
    public void Apply(GameFocusOverlaySettings settings, GameFocusOverlayFrame frame)
    {
        if (_disposed) return;
        var previouslyVisible = ShouldShow;
        var next = settings.Normalize();
        var contentChanged = !SameFrame(_frame, frame);
        _layoutDirty |= next != _settings || contentChanged;
        _styleDirty |= next != _settings || contentChanged;
        _settings = next; _frame = frame;
        if (!ShouldShow)
        {
            DestroyVisuals();
            _images.Apply("", Array.Empty<string>());
        }
        else if (contentChanged || !previouslyVisible)
            _images.Apply(frame.MediaGeneration, ImageKeys(frame));
    }

    // 是否保留面板由启用状态与经营生命周期决定，不能再由当前订单数量决定。
    // 零订单、显示过滤后零行或短暂没有完整订单时，只隐藏卡片并显示空态；不借用旧订单补位。
    private bool ShouldShow => _settings.Enabled && _frame.Visible && _frame.Generation > 0;
    private float Scale => _settings.FontSize / 16f;
    private bool Dark => _settings.Theme == "dark";

    /// <summary>稳定帧只核对屏幕尺寸和有界图片作业。无鼠标、键盘、暂停状态、标题提示或交互分支。</summary>
    public void Tick()
    {
        if (_disposed || !ShouldShow) return;
        try
        {
            if (_root == null)
            {
                if (Time.unscaledTime < _nextRetryAt) return;
                CreateVisuals();
            }
            var width = Math.Max(1, Screen.width); var height = Math.Max(1, Screen.height);
            if (_screenWidth != width || _screenHeight != height)
            { _screenWidth = width; _screenHeight = height; _layoutDirty = true; }
            if (_layoutDirty)
            {
                _rect = GameFocusOverlayGeometry.Resolve(_settings.Layout, width, height);
                LayoutVisuals(); _layoutDirty = false;
            }
            if (_styleDirty) { ApplyStyle(); _styleDirty = false; }
            if (_images.Tick()) RefreshImages();
            if (_root != null && !_root.activeSelf) _root.SetActive(true);
        }
        catch (Exception error)
        {
            DestroyVisuals(); _nextRetryAt = Time.unscaledTime + 5f;
            var reason = error.GetBaseException().Message;
            if (_lastFailure != reason)
            { _lastFailure = reason; _failure?.Invoke("游戏内专注显示暂不可用：" + reason); }
        }
    }

    private static IEnumerable<string> ImageKeys(GameFocusOverlayFrame frame)
    {
        foreach (var row in frame.Rows.Take(64))
        {
            foreach (var key in row.AvatarKeys.Take(4)) yield return key;
            foreach (var tag in row.Badges.Concat(row.Groups.Take(16).SelectMany(group => group.Tags.Take(32))))
                if (tag.IconKey != null) yield return tag.IconKey;
        }
    }

    private void CreateVisuals()
    {
        _members ??= UiMembers.Resolve();
        _font = _members.CreateFont.Invoke(null, new object[] { "Microsoft YaHei", 18 })
            ?? throw new InvalidOperationException("中文字体创建失败");
        CreateShape();
        _root = CreateObject("MystiaStewardCompanion.GameFocus", null, _members.Canvas);
        _root.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = Component(_root, _members.Canvas);
        _members.SetEnum(canvas, "renderMode", 0);
        _members.Set(canvas, "overrideSorting", true); _members.Set(canvas, "sortingOrder", 32760);
        _panel = CreateImage("Panel", _root.transform, rounded: true);
        _viewport = RectOf(CreateObject("ContentViewport", _panel.Root.transform, _members.Mask));
        _emptyState = CreateText("EmptyState", _viewport, false);
        SetText(_emptyState, "当前无可显示订单");
        _members.SetEnum(_emptyState.Component, "alignment", 4); // MiddleCenter，仅正文空态，不新增顶栏。
        foreach (var edge in new[] { "Top", "Bottom", "Left", "Right" })
            _borders.Add(CreateImage("Border" + edge, _panel.Root.transform));
        _layoutDirty = _styleDirty = true;
    }

    /// <summary>
    /// 所有圆角使用本组件一次性生成的白色九宫格精灵，不读取/复制游戏UI纹理。
    /// 静态形状只含alpha，颜色由Image指定；非可读纹理在创建后释放CPU副本，随组件统一销毁。
    /// </summary>
    private void CreateShape()
    {
        const int size = 16; const float radius = 5;
        _shapeTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var pixels = new Il2CppStructArray<Color32>(size * size);
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var dx = Math.Max(0, Math.Abs(x + .5f - size / 2f) - (size / 2f - radius));
            var dy = Math.Max(0, Math.Abs(y + .5f - size / 2f) - (size / 2f - radius));
            var alpha = Math.Clamp(radius + .5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
        }
        _shapeTexture.SetPixels32(pixels); _shapeTexture.Apply(false, true);
        _shape = Sprite.Create(_shapeTexture, new Rect(0, 0, size, size), new Vector2(.5f, .5f),
            100, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius), false);
        if (_shape == null) throw new InvalidOperationException("专注卡片圆角创建失败");
        _shape.hideFlags = HideFlags.HideAndDontSave;
        // 独立一像素圆角轮廓避免把半透明边框当作整块底图，再与填充叠加后错误加深标签背景。
        _outlineTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var dx = Math.Max(0, Math.Abs(x + .5f - size / 2f) - (size / 2f - radius));
            var dy = Math.Max(0, Math.Abs(y + .5f - size / 2f) - (size / 2f - radius));
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            var alpha = Math.Clamp(radius + .5f - distance, 0, 1) - Math.Clamp(radius - .5f - distance, 0, 1);
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Math.Max(0, alpha) * 255));
        }
        _outlineTexture.SetPixels32(pixels); _outlineTexture.Apply(false, true);
        _outline = Sprite.Create(_outlineTexture, new Rect(0, 0, size, size), new Vector2(.5f, .5f),
            100, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius), false);
        if (_outline == null) throw new InvalidOperationException("专注标签边框创建失败");
        _outline.hideFlags = HideFlags.HideAndDontSave;
    }

    private void LayoutVisuals()
    {
        if (_panel == null || _viewport == null) return;
        var inset = 8 * Scale;
        var width = Math.Max(1, (float)_rect.Width - 2 * inset);
        var height = Math.Max(1, (float)_rect.Height - 2 * inset);
        SetRect(_panel.Rect, (float)_rect.X, (float)_rect.Y, (float)_rect.Width, (float)_rect.Height);
        SetRect(_viewport, inset, inset, width, height);
        var count = Math.Min(64, _frame.Rows.Count);
        if (_emptyState != null)
        {
            // 空态与卡片共用固定正文视口，订单进出不改变外壳、边框、透明度或图标缓存代次。
            _emptyState.Root.SetActive(count == 0);
            SetFont(_emptyState, Math.Max(10, (int)Math.Round(_settings.FontSize * .8125f)));
            SetRect(_emptyState.Rect, 0, 0, width, height);
        }
        while (_cards.Count < count) _cards.Add(CreateCard());
        var y = 0f;
        for (var index = 0; index < _cards.Count; index++)
        {
            var card = _cards[index]; card.Background.Root.SetActive(index < count);
            if (index >= count) continue;
            card.Y = y; card.Height = LayoutCard(card, _frame.Rows[index], width);
            y += card.Height + 8 * Scale;
        }
        var offset = Math.Max(0, y - 8 * Scale - height) * _settings.ScrollPercent / 100f;
        for (var index = 0; index < count; index++)
            SetRect(_cards[index].Background.Rect, 0, _cards[index].Y - offset, width, _cards[index].Height);
        var border = _settings.BorderWidth;
        SetRect(_borders[0].Rect, 0, 0, (float)_rect.Width, border);
        SetRect(_borders[1].Rect, 0, (float)_rect.Height - border, (float)_rect.Width, border);
        SetRect(_borders[2].Rect, 0, 0, border, (float)_rect.Height);
        SetRect(_borders[3].Rect, (float)_rect.Width - border, 0, border, (float)_rect.Height);
        foreach (var edge in _borders) edge.Root.SetActive(border > 0);
        _styleDirty = true; RefreshImages();
    }

    private CardVisual CreateCard()
    {
        var background = CreateImage("OrderCard", _viewport!, rounded: true);
        var heading = CreateText("OrderHeading", background.Root.transform, true);
        _members!.SetEnum(heading.Component, "horizontalOverflow", 1); // 单行省略由真实字宽测量完成。
        _members.SetEnum(heading.Component, "verticalOverflow", 0);
        var separator = CreateImage("OrderSeparator", background.Root.transform);
        return new(background, heading, separator);
    }

    private float LayoutCard(CardVisual visual, GameFocusOverlayRow row, float available)
    {
        visual.Row = row;
        var padX = 8 * Scale; var padY = 10 * Scale; var gap = 5 * Scale;
        var width = Math.Max(1, available - 2 * padX);
        var avatarSize = 28 * Scale;
        // 限制异常投影最多四张头像；现有常态只有明确的一张头像，不以名称推导额外人物。
        var avatars = Math.Max(1, Math.Min(4, row.AvatarKeys.Count));
        while (visual.Avatars.Count < avatars) visual.Avatars.Add(CreateIcon("GuestAvatar", visual.Background.Root.transform));
        for (var i = 0; i < visual.Avatars.Count; i++)
        {
            var avatar = visual.Avatars[i]; avatar.Root.SetActive(i < avatars);
            if (i < avatars) { avatar.Key = i < row.AvatarKeys.Count ? row.AvatarKeys[i] : null; PositionIcon(avatar, padX + i * (avatarSize + gap), padY, avatarSize); }
        }
        var avatarWidth = avatars == 0 ? 0 : avatars * (avatarSize + gap);
        var badgeCount = Math.Min(4, row.Badges.Count);
        while (visual.Badges.Count < badgeCount) visual.Badges.Add(CreateTag(visual.Background.Root.transform));
        var badgeWidth = 0f; var badgeHeight = 30 * Scale;
        for (var i = 0; i < visual.Badges.Count; i++)
        {
            var badge = visual.Badges[i]; badge.Border.Root.SetActive(i < badgeCount);
            if (i >= badgeCount) continue;
            // 标题状态优先保留自然宽度，不按卡片宽度百分比压成两行；姓名使用剩余宽度省略。
            LayoutTag(badge, row.Badges[i], width);
            badgeWidth += badge.Width + (i == 0 ? 0 : gap); badgeHeight = Math.Max(badgeHeight, badge.Height);
        }
        // 标题与Flutter保持单行省略，不能因长姓名或状态占宽而挤高正文。未知头像也保留同一槽位。
        var headingWidth = Math.Max(1, width - avatarWidth - (badgeCount == 0 ? 0 : badgeWidth + gap));
        SetFont(visual.Heading, _settings.FontSize);
        FitHeading(visual.Heading, row.Heading, headingWidth);
        var headingHeight = _settings.FontSize * 1.45f;
        var headerHeight = Math.Max(Math.Max(avatarSize, badgeHeight), headingHeight);
        SetRect(visual.Heading.Rect, padX + avatarWidth, padY + Math.Max(0, (headerHeight - headingHeight) / 2), headingWidth, headingHeight);
        var badgeX = padX + width - badgeWidth;
        for (var i = 0; i < badgeCount; i++)
        {
            var badge = visual.Badges[i];
            SetRect(badge.Border.Rect, badgeX, padY + (headerHeight - badge.Height) / 2, badge.Width, badge.Height);
            badgeX += badge.Width + gap;
        }
        var groupCount = Math.Min(16, row.Groups.Count);
        while (visual.Groups.Count < groupCount) visual.Groups.Add(CreateGroup(visual.Background.Root.transform));
        var y = padY + headerHeight;
        for (var i = 0; i < visual.Groups.Count; i++)
        {
            var group = visual.Groups[i]; group.Root.SetActive(i < groupCount);
            if (i >= groupCount) continue;
            y += gap;
            var h = LayoutGroup(group, row.Groups[i], width);
            SetRect(group.Rect, padX, y, width, h); y += h;
        }
        var height = y + padY;
        SetRect(visual.Separator.Rect, padX, height - 1, width, 1);
        return height;
    }

    private GroupVisual CreateGroup(Transform parent)
    {
        var root = CreateObject("InfoGroup", parent);
        return new(root, RectOf(root), CreateText("Prefix", root.transform, false));
    }

    private float LayoutGroup(GroupVisual visual, GameFocusOverlayGroup group, float width)
    {
        var prefixWidth = 24 * Scale; var prefixGap = 8 * Scale; var gap = 5 * Scale;
        var tagsWidth = Math.Max(1, width - prefixWidth - prefixGap);
        SetText(visual.Prefix, group.Prefix); SetFont(visual.Prefix, TagFontSize);
        var prefixHeight = MeasureHeight(visual.Prefix, prefixWidth);
        var count = Math.Min(32, group.Tags.Count);
        while (visual.Tags.Count < count) visual.Tags.Add(CreateTag(visual.Root.transform));
        var x = 0f; var y = 0f; var lineHeight = 0f; var firstHeight = 30 * Scale;
        for (var i = 0; i < visual.Tags.Count; i++)
        {
            var tag = visual.Tags[i]; tag.Border.Root.SetActive(i < count);
            if (i >= count) continue;
            LayoutTag(tag, group.Tags[i], tagsWidth);
            if (x > 0 && x + tag.Width > tagsWidth + .01f) { y += lineHeight + gap; x = 0; lineHeight = 0; }
            if (i == 0) firstHeight = tag.Height;
            SetRect(tag.Border.Rect, prefixWidth + prefixGap + x, y, tag.Width, tag.Height);
            x += tag.Width + gap; lineHeight = Math.Max(lineHeight, tag.Height);
        }
        SetRect(visual.Prefix.Rect, 0, Math.Max(0, (firstHeight - prefixHeight) / 2), prefixWidth, prefixHeight);
        return Math.Max(prefixHeight, y + lineHeight);
    }

    private int TagFontSize => Math.Max(9, (int)Math.Round(_settings.FontSize * .75f));

    private TagVisual CreateTag(Transform parent)
    {
        var border = CreateImage("TagBorder", parent, rounded: true);
        _members!.Set(border.Component, "sprite", _outline);
        var fill = CreateImage("TagFill", border.Root.transform, rounded: true);
        return new(border, fill, CreateText("TagLabel", border.Root.transform, true), CreateIcon("TagIcon", border.Root.transform));
    }

    private void LayoutTag(TagVisual visual, GameFocusOverlayTag tag, float available)
    {
        visual.Tag = tag;
        var padX = 8 * Scale; var padY = 5 * Scale; var iconSize = 20 * Scale; var gap = 5 * Scale;
        var hasIcon = tag.IconKey != null;
        var iconWidth = hasIcon ? iconSize + gap : 0;
        SetFont(visual.Label, TagFontSize); SetText(visual.Label, tag.Text);
        var preferred = Convert.ToSingle(_members!.Get(visual.Label.Component, "preferredWidth"));
        var maximum = Math.Max(1, available);
        visual.Width = Math.Min(maximum, Math.Max(1, preferred + padX * 2 + iconWidth));
        var textWidth = Math.Max(1, visual.Width - padX * 2 - iconWidth);
        var textHeight = MeasureHeight(visual.Label, textWidth);
        visual.Height = Math.Max(iconSize, textHeight) + padY * 2;
        SetRect(visual.Fill.Rect, 1, 1, Math.Max(0, visual.Width - 2), visual.Height - 2);
        SetRect(visual.Label.Rect, padX + iconWidth, padY + Math.Max(0, (iconSize - textHeight) / 2), textWidth, textHeight);
        visual.Icon.Root.SetActive(hasIcon); visual.Icon.Key = tag.IconKey;
        if (hasIcon) PositionIcon(visual.Icon, padX, padY, iconSize);
    }

    private float MeasureHeight(TextVisual text, float width)
    {
        SetRect(text.Rect, 0, 0, width, 1);
        var result = Convert.ToSingle(_members!.Get(text.Component, "preferredHeight"));
        return float.IsFinite(result) ? Math.Max(text.FontSize * 1.4f, result) : text.FontSize * 1.4f;
    }

    /// <summary>
    /// 使用同一UGUI字体实测单行宽度后省略，不用字符串长度估算中文/拉丁宽度。
    /// 截断位置按Unicode字素边界选择，避免割裂代理对或组合字符；只有内容/布局变化才调用。
    /// </summary>
    private void FitHeading(TextVisual text, string value, float width)
    {
        value = value.Replace('\n', ' ').Replace('\r', ' ');
        SetText(text, value);
        float Measured() => Convert.ToSingle(_members!.Get(text.Component, "preferredWidth"));
        if (Measured() <= width) return;
        const string ellipsis = "…";
        SetText(text, ellipsis);
        if (Measured() > width) { SetText(text, ""); return; }
        var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        var low = 0; var high = boundaries.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var end = middle < boundaries.Length ? boundaries[middle] : value.Length;
            SetText(text, value[..end] + ellipsis);
            if (Measured() <= width) low = middle; else high = middle - 1;
        }
        SetText(text, value[..(low < boundaries.Length ? boundaries[low] : value.Length)] + ellipsis);
    }

    private void ApplyStyle()
    {
        if (_panel == null) return;
        Colorize(_panel.Component, Dark ? 0x1e1d1b : 0xf4f1e9, BackgroundAlpha);
        var foreground = Dark ? 0xf0e9df : 0x342f29;
        var muted = Dark ? 0xc1b7a9 : 0x726859;
        var border = Dark ? 0x554b40 : 0xd8cebd;
        if (_emptyState != null) Colorize(_emptyState.Component, muted, ForegroundAlpha);
        foreach (var edge in _borders) Colorize(edge.Component, border, BackgroundAlpha);
        foreach (var card in _cards)
        {
            if (!card.Background.Root.activeSelf || card.Row == null) continue;
            Colorize(card.Background.Component, ParseColor(card.Row.HighlightHex), .11f * BackgroundAlpha);
            Colorize(card.Heading.Component, foreground, ForegroundAlpha);
            Colorize(card.Separator.Component, border, BackgroundAlpha);
            foreach (var avatar in card.Avatars) StyleIcon(avatar, muted);
            foreach (var badge in card.Badges) StyleTag(badge);
            foreach (var group in card.Groups)
            {
                Colorize(group.Prefix.Component, muted, ForegroundAlpha);
                foreach (var tag in group.Tags) StyleTag(tag);
            }
        }
    }
    private float BackgroundAlpha => _settings.BackgroundOpacity / 100f;
    private float ForegroundAlpha => _settings.ContentOpacity / 100f;
    private void StyleTag(TagVisual tag)
    {
        var tone = Tone(tag.Tag?.Tone ?? GameFocusOverlayTone.Neutral);
        // 标签边界和底色是分别绘制的圆角图形；透明度不叠乘文字/图片，遵循Flutter组件同一语义。
        Colorize(tag.Border.Component, tone, (Dark ? .33f : .23f) * ForegroundAlpha);
        Colorize(tag.Fill.Component, tone, (Dark ? .10f : .07f) * BackgroundAlpha);
        Colorize(tag.Label.Component, tone, ForegroundAlpha); StyleIcon(tag.Icon, tone);
    }
    private void StyleIcon(IconVisual icon, int tone)
    { Colorize(icon.Raw, 0xffffff, ForegroundAlpha); Colorize(icon.Fallback.Component, tone, ForegroundAlpha); }
    private int Tone(GameFocusOverlayTone tone) => (tone, Dark) switch
    {
        (GameFocusOverlayTone.Recipe, false) => 0x8b4328, (GameFocusOverlayTone.Recipe, true) => 0xffba94,
        (GameFocusOverlayTone.Ingredient, false) => 0x416336, (GameFocusOverlayTone.Ingredient, true) => 0xa6d291,
        (GameFocusOverlayTone.Extra, false) => 0x71538b, (GameFocusOverlayTone.Extra, true) => 0xd1baec,
        (GameFocusOverlayTone.Cooker, false) => 0x4f6377, (GameFocusOverlayTone.Cooker, true) => 0xb1cbe4,
        (GameFocusOverlayTone.Beverage, false) => 0x276a74, (GameFocusOverlayTone.Beverage, true) => 0x95d5d4,
        (GameFocusOverlayTone.Request, false) => 0x805d25, (GameFocusOverlayTone.Request, true) => 0xe5cb91,
        (GameFocusOverlayTone.Warning, false) => 0x965230, (GameFocusOverlayTone.Warning, true) => 0xffc195,
        (GameFocusOverlayTone.Success, false) => 0x30694e, (GameFocusOverlayTone.Success, true) => 0x9fddbb,
        (_, false) => 0x726859, _ => 0xc1b7a9,
    };
    private static int ParseColor(string? hex) => hex is { Length: 7 } && hex[0] == '#'
        && int.TryParse(hex[1..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : 0x929aa6;
    private void Colorize(object graphic, int rgb, float alpha) => _members!.Set(graphic, "color",
        new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha));

    private IconVisual CreateIcon(string name, Transform parent)
    {
        var root = CreateObject(name, parent);
        var image = CreateObject("EntityImage", root.transform, _members!.RawImage);
        var raw = Component(image, _members.RawImage); _members.Set(raw, "raycastTarget", false);
        var fallback = CreateText("ImagePlaceholder", root.transform, false); SetText(fallback, "◇");
        _members.SetEnum(fallback.Component, "alignment", 4); // MiddleCenter，不影响相邻文字基线。
        return new(root, RectOf(root), image, RectOf(image), raw, fallback);
    }
    private void PositionIcon(IconVisual icon, float x, float y, float size)
    {
        icon.Size = size; SetRect(icon.Rect, x, y, size, size);
        SetFont(icon.Fallback, TagFontSize); SetRect(icon.Fallback.Rect, 0, 0, size, size);
    }
    private void RefreshImages()
    {
        foreach (var card in _cards)
        {
            if (!card.Background.Root.activeSelf) continue;
            foreach (var avatar in card.Avatars) RefreshImage(avatar);
            foreach (var badge in card.Badges) RefreshImage(badge.Icon);
            foreach (var group in card.Groups) foreach (var tag in group.Tags) RefreshImage(tag.Icon);
        }
    }
    private void RefreshImage(IconVisual icon)
    {
        if (!icon.Root.activeSelf) return;
        var texture = _images.Get(icon.Key);
        _members!.Set(icon.Raw, "texture", texture);
        icon.ImageRoot.SetActive(texture != null); icon.Fallback.Root.SetActive(texture == null);
        // 槽位在加载前后完全相同，仅在内部等比缩放PNG，人物头像也不会被拉扁或挤动标签文字。
        var width = icon.Size; var height = icon.Size;
        if (texture != null)
        {
            var scale = icon.Size / Math.Max(texture.width, texture.height);
            width = texture.width * scale; height = texture.height * scale;
        }
        SetRect(icon.ImageRect, (icon.Size - width) / 2, (icon.Size - height) / 2, width, height);
    }

    private ImageVisual CreateImage(string name, Transform parent, bool rounded = false)
    {
        var root = CreateObject(name, parent, _members!.Image);
        var graphic = Component(root, _members.Image); _members.Set(graphic, "raycastTarget", false);
        if (rounded) { _members.Set(graphic, "sprite", _shape); _members.SetEnum(graphic, "type", 1); }
        return new(root, RectOf(root), graphic);
    }
    private TextVisual CreateText(string name, Transform parent, bool bold)
    {
        var root = CreateObject(name, parent, _members!.Text); var text = Component(root, _members.Text);
        _members.Set(text, "font", _font); _members.Set(text, "supportRichText", false); _members.Set(text, "raycastTarget", false);
        _members.Set(text, "lineSpacing", 1.1f); _members.SetEnum(text, "alignment", 0); _members.SetEnum(text, "fontStyle", bold ? 1 : 0);
        _members.SetEnum(text, "horizontalOverflow", 0); _members.SetEnum(text, "verticalOverflow", 1);
        return new(root, RectOf(root), text);
    }
    private static GameObject CreateObject(string name, Transform? parent, params Type[] types)
    {
        var native = new Il2CppReferenceArray<Il2CppSystem.Type>(types.Length + 1); native[0] = Il2CppType.From(typeof(RectTransform));
        for (var index = 0; index < types.Length; index++) native[index + 1] = Il2CppType.From(types[index]);
        var result = new GameObject(name, native); if (parent != null) result.transform.SetParent(parent, false); return result;
    }
    private static object Component(GameObject root, Type type) => RuntimeReflectionUtility.TryCastRuntimeObject(
        root.GetComponent(Il2CppType.From(type)), type.FullName!) ?? throw new InvalidOperationException("无法创建专注组件 " + type.FullName);
    private static RectTransform RectOf(GameObject root) => (RectTransform)Component(root, typeof(RectTransform));
    private static void SetRect(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(Math.Max(0, width), Math.Max(0, height));
    }
    private void SetFont(TextVisual text, int size) { text.FontSize = size; _members!.Set(text.Component, "fontSize", size); }
    private void SetText(TextVisual text, string value) { if (text.Value == value) return; text.Value = value; _members!.Set(text.Component, "text", value); }

    private static bool SameFrame(GameFocusOverlayFrame left, GameFocusOverlayFrame right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Visible != right.Visible || left.Generation != right.Generation || left.MediaGeneration != right.MediaGeneration || left.Rows.Count != right.Rows.Count) return false;
        for (var i = 0; i < left.Rows.Count; i++)
        {
            var a = left.Rows[i]; var b = right.Rows[i];
            if (a.Identity != b.Identity || a.Heading != b.Heading || a.HighlightHex != b.HighlightHex || !a.AvatarKeys.SequenceEqual(b.AvatarKeys)
                || !a.Badges.SequenceEqual(b.Badges) || a.Groups.Count != b.Groups.Count) return false;
            for (var j = 0; j < a.Groups.Count; j++)
                if (a.Groups[j].Prefix != b.Groups[j].Prefix || !a.Groups[j].Tags.SequenceEqual(b.Groups[j].Tags)) return false;
        }
        return true;
    }
    private void DestroyVisuals()
    {
        if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
        if (_font is UnityEngine.Object font && font != null) UnityEngine.Object.Destroy(font);
        if (_shape != null) UnityEngine.Object.Destroy(_shape);
        if (_shapeTexture != null) UnityEngine.Object.Destroy(_shapeTexture);
        if (_outline != null) UnityEngine.Object.Destroy(_outline);
        if (_outlineTexture != null) UnityEngine.Object.Destroy(_outlineTexture);
        _root = null; _panel = null; _viewport = null; _emptyState = null; _font = null; _shape = null; _shapeTexture = null; _outline = null; _outlineTexture = null;
        _cards.Clear(); _borders.Clear();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; DestroyVisuals(); _images.Dispose(); }

    private sealed class TextVisual
    {
        public TextVisual(GameObject root, RectTransform rect, object component) { Root = root; Rect = rect; Component = component; }
        public readonly GameObject Root; public readonly RectTransform Rect; public readonly object Component;
        public string Value = ""; public int FontSize;
    }
    private sealed record ImageVisual(GameObject Root, RectTransform Rect, object Component);
    private sealed class IconVisual
    {
        public IconVisual(GameObject root, RectTransform rect, GameObject imageRoot, RectTransform imageRect, object raw, TextVisual fallback)
        { Root = root; Rect = rect; ImageRoot = imageRoot; ImageRect = imageRect; Raw = raw; Fallback = fallback; }
        public readonly GameObject Root, ImageRoot; public readonly RectTransform Rect, ImageRect; public readonly object Raw; public readonly TextVisual Fallback;
        public string? Key; public float Size;
    }
    private sealed class TagVisual
    {
        public TagVisual(ImageVisual border, ImageVisual fill, TextVisual label, IconVisual icon) { Border = border; Fill = fill; Label = label; Icon = icon; }
        public readonly ImageVisual Border, Fill; public readonly TextVisual Label; public readonly IconVisual Icon;
        public GameFocusOverlayTag? Tag; public float Width, Height;
    }
    private sealed class GroupVisual
    {
        public GroupVisual(GameObject root, RectTransform rect, TextVisual prefix) { Root = root; Rect = rect; Prefix = prefix; }
        public readonly GameObject Root; public readonly RectTransform Rect; public readonly TextVisual Prefix;
        public readonly List<TagVisual> Tags = new();
    }
    private sealed class CardVisual
    {
        public CardVisual(ImageVisual background, TextVisual heading, ImageVisual separator) { Background = background; Heading = heading; Separator = separator; }
        public readonly ImageVisual Background, Separator; public readonly TextVisual Heading;
        public readonly List<IconVisual> Avatars = new(); public readonly List<TagVisual> Badges = new(); public readonly List<GroupVisual> Groups = new();
        public GameFocusOverlayRow? Row; public float Y, Height;
    }
    /// <summary>缓存明确的UGUI公开属性元数据；不建立任何输入系统、游戏节点或原生业务方法的反射入口。</summary>
    private sealed class UiMembers
    {
        public Type Canvas = null!, Image = null!, RawImage = null!, Text = null!, Mask = null!;
        public MethodInfo CreateFont = null!;
        private readonly Dictionary<(Type, string), PropertyInfo> _properties = new();
        public static UiMembers Resolve()
        {
            static Type Exact(string name) => RuntimeReflectionUtility.FindType(name) ?? throw new InvalidOperationException("缺少UI类型 " + name);
            var font = Exact("UnityEngine.Font");
            return new() {
                Canvas = Exact("UnityEngine.Canvas"), Image = Exact("UnityEngine.UI.Image"), RawImage = Exact("UnityEngine.UI.RawImage"),
                Text = Exact("UnityEngine.UI.Text"), Mask = Exact("UnityEngine.UI.RectMask2D"),
                CreateFont = font.GetMethod("CreateDynamicFontFromOSFont", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(int) }, null)
                    ?? throw new InvalidOperationException("缺少精确中文字体创建接口"),
            };
        }
        private PropertyInfo Property(object target, string name)
        {
            var key = (target.GetType(), name); if (_properties.TryGetValue(key, out var value)) return value;
            value = key.Item1.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new InvalidOperationException("缺少UI属性 " + name);
            _properties.Add(key, value); return value;
        }
        public void Set(object target, string name, object? value) => Property(target, name).SetValue(target, value);
        public object? Get(object target, string name) => Property(target, name).GetValue(target);
        public void SetEnum(object target, string name, int value)
        { var property = Property(target, name); property.SetValue(target, Enum.ToObject(property.PropertyType, value)); }
    }
}
