namespace MystiaStewardCompanion.Ui;

/// <summary>游戏内专注层的外观由外部客户端配置；游戏内始终只读，不存在编辑会话或输入门槛。</summary>
internal sealed record GameFocusOverlaySettings
{
    public bool Enabled { get; init; }
    public int BackgroundOpacity { get; init; } = 72;
    public int ContentOpacity { get; init; } = 100;
    public int BorderWidth { get; init; } = 1;
    public int FontSize { get; init; } = 16;
    public int ScrollPercent { get; init; }
    public string Theme { get; init; } = "light";
    public GameFocusOverlayContent Content { get; init; } = new();
    public GameFocusOverlayLayout Layout { get; init; } = GameFocusOverlayLayout.Default;

    /// <summary>渲染末端的防御性归一化；API仍应独立拒绝非法输入，不能借此静默接受坏配置。</summary>
    public GameFocusOverlaySettings Normalize() => this with
    {
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 100),
        ContentOpacity = Math.Clamp(ContentOpacity, 20, 100),
        BorderWidth = Math.Clamp(BorderWidth, 0, 6),
        FontSize = Math.Clamp(FontSize, 12, 24),
        ScrollPercent = Math.Clamp(ScrollPercent, 0, 100),
        Theme = Theme == "dark" ? "dark" : "light",
        Content = Content ?? new(),
        Layout = Layout.Normalize(),
    };
}

/// <summary>与Flutter专注显示共享的内容选择；只控制显示，不改变订单参与或自动化配置。</summary>
internal sealed record GameFocusOverlayContent
{
    public bool ShowRare { get; init; } = true;
    public bool ShowNormal { get; init; } = true;
    public bool ShowOrderTags { get; init; } = true;
    public bool ShowFood { get; init; } = true;
    public bool ShowIngredients { get; init; } = true;
    public bool ShowExtras { get; init; } = true;
    public bool ShowCooker { get; init; } = true;
    public bool ShowBeverage { get; init; } = true;
    public bool RareFirst { get; init; }
}

/// <summary>左上角为原点的归一化布局；存储比例而不是机器分辨率，缩放或换屏后仍可找回。</summary>
internal readonly record struct GameFocusOverlayLayout(double X, double Y, double Width, double Height)
{
    public static GameFocusOverlayLayout Default => new(.7, .12, .28, .72);

    public GameFocusOverlayLayout Normalize()
    {
        static double Valid(double value, double fallback) => double.IsFinite(value) ? value : fallback;
        // 比例下限只防止零/非法值；可读像素下限交给Geometry。固定5%会使8K屏幕
        // 上的260px窗口保存为约3.4%后，下次加载又被放大到384px，破坏布局往返。
        var width = Math.Clamp(Valid(Width, Default.Width), .000001, 1);
        var height = Math.Clamp(Valid(Height, Default.Height), .000001, 1);
        return new(Math.Clamp(Valid(X, Default.X), 0, 1 - width),
            Math.Clamp(Valid(Y, Default.Y), 0, 1 - height), width, height);
    }
}

/// <summary>固定视觉语义映射Flutter OrderTagKind；颜色不是业务类别或推荐规则的输入。</summary>
internal enum GameFocusOverlayTone { Recipe, Ingredient, Extra, Cooker, Beverage, Request, Warning, Success, Neutral }

/// <summary>图标只接受既有资源清单的精确键；缺图时保留同尺寸占位与原文字。</summary>
internal sealed record GameFocusOverlayTag(string Text, GameFocusOverlayTone Tone, string? IconKey = null);
internal sealed record GameFocusOverlayGroup(string Prefix, IReadOnlyList<GameFocusOverlayTag> Tags);

/// <summary>不可变托管卡片数据。标题状态、正文标签与实体图片分离，不解析拼接文本猜测业务含义。</summary>
internal sealed record GameFocusOverlayRow(string Identity, string Heading, IReadOnlyList<string> AvatarKeys,
    IReadOnlyList<GameFocusOverlayTag> Badges, string? HighlightHex, IReadOnlyList<GameFocusOverlayGroup> Groups);

/// <summary>场景代次决定内容归属，资源代次决定图片归属；不把上一代图像冒充当前资源。</summary>
internal sealed record GameFocusOverlayFrame(bool Visible, long Generation, string MediaGeneration,
    IReadOnlyList<GameFocusOverlayRow> Rows)
{
    public static GameFocusOverlayFrame Hidden { get; } = new(false, 0, "", Array.Empty<GameFocusOverlayRow>());
}

internal readonly record struct GameFocusOverlayRect(double X, double Y, double Width, double Height);

/// <summary>
/// 布局运算不依赖Unity，可离线验证外部配置和小分辨率。尺寸下限在屏幕较小时随视口收紧，
/// 游戏内不提供拖动、缩放或滚轮操作，所有位置与尺寸均来自外部配置。
/// </summary>
internal static class GameFocusOverlayGeometry
{
    public static GameFocusOverlayRect Resolve(GameFocusOverlayLayout layout, int width, int height)
    {
        var safe = layout.Normalize();
        return Clamp(new(safe.X * width, safe.Y * height, safe.Width * width, safe.Height * height), width, height);
    }

    public static GameFocusOverlayRect Clamp(GameFocusOverlayRect rect, int width, int height)
    {
        var availableWidth = Math.Max(1, width);
        var availableHeight = Math.Max(1, height);
        static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
        var w = Math.Clamp(Finite(rect.Width, 320), Math.Min(260, availableWidth), availableWidth);
        var h = Math.Clamp(Finite(rect.Height, 240), Math.Min(140, availableHeight), availableHeight);
        return new(Math.Clamp(Finite(rect.X, 0), 0, availableWidth - w),
            Math.Clamp(Finite(rect.Y, 0), 0, availableHeight - h), w, h);
    }

    public static GameFocusOverlayLayout Normalize(GameFocusOverlayRect rect, int width, int height)
    {
        var safe = Clamp(rect, width, height);
        return new(safe.X / Math.Max(1, width), safe.Y / Math.Max(1, height),
            safe.Width / Math.Max(1, width), safe.Height / Math.Max(1, height));
    }

}
