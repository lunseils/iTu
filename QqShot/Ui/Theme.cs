using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace QqShot.Ui;

/// <summary>
/// 全局视觉规范：柔和蓝灰底色 + 圆润无锐利边角 + 低饱和强调色。
/// 所有界面颜色、圆角与阴影都从这里取，保证整体观感一致。
/// </summary>
public static class Theme
{
    // 背景 / 面板
    public static readonly Color Canvas = Color.FromArgb(244, 246, 250);
    public static readonly Color Surface = Color.FromArgb(255, 255, 255);
    public static readonly Color SurfaceAlt = Color.FromArgb(250, 251, 253);
    public static readonly Color SurfaceHover = Color.FromArgb(240, 244, 252);
    public static readonly Color Border = Color.FromArgb(226, 231, 239);
    public static readonly Color BorderStrong = Color.FromArgb(205, 213, 226);

    // 文本
    public static readonly Color Text = Color.FromArgb(41, 50, 66);
    public static readonly Color TextSecondary = Color.FromArgb(108, 120, 138);
    public static readonly Color TextMuted = Color.FromArgb(136, 148, 166);
    public static readonly Color TextOnAccent = Color.White;

    // 强调
    public static readonly Color Accent = Color.FromArgb(94, 143, 246);
    public static readonly Color AccentSoft = Color.FromArgb(232, 240, 254);

    // 语义
    public static readonly Color Danger = Color.FromArgb(240, 118, 114);
    public static readonly Color Success = Color.FromArgb(79, 178, 154);
    public static readonly Color Warning = Color.FromArgb(240, 178, 86);

    // 遮罩（截图浮层）
    public static readonly Color MaskFill = Color.FromArgb(96, 20, 24, 33);
    public static readonly Color MaskActiveFill = Color.FromArgb(28, 20, 24, 33);

    // 标注默认色板（低饱和、柔和）
    public static readonly Color[] ToolPalette =
    {
        Color.FromArgb(240, 118, 114), // 珊瑚红
        Color.FromArgb(240, 178, 86),  // 琥珀
        Color.FromArgb(94, 143, 246),  // 主蓝
        Color.FromArgb(79, 178, 154),  // 青绿
        Color.FromArgb(158, 128, 240), // 紫
        Color.FromArgb(43, 52, 66),    // 墨
        Color.FromArgb(255, 255, 255)  // 白
    };

    // ---- 胶囊（工具条 / 调节条 / 菜单）专用令牌 ----
    //
    // 这几个值是照着成品胶囊定的：白面、1px 冷灰描边、图标近黑但不用纯黑
    // （纯黑在小尺寸下显硬），悬停用极淡的蓝灰，激活用低饱和主色。

    /// <summary>图标主色（比正文略浅，避免小图形发死）。</summary>
    public static readonly Color Icon = Color.FromArgb(48, 58, 74);

    // ---- 旧版图标复刻用的原始配色（从原图取样，保证与旧版一致）----

    /// <summary>普通图标：旧版是纯黑。</summary>
    public static readonly Color PillInk = Color.FromArgb(0, 0, 0);

    /// <summary>关闭按钮：旧版是亮红。</summary>
    public static readonly Color PillClose = Color.FromArgb(245, 34, 0);

    /// <summary>确认按钮：旧版是绿。</summary>
    public static readonly Color PillConfirm = Color.FromArgb(0, 204, 102);

    /// <summary>不可用图标的透明度（与旧版一致）。</summary>
    public const float PillDisabledAlpha = 0.26f;

    /// <summary>格位悬停底色。</summary>
    public static readonly Color CapsuleHover = Color.FromArgb(16, 20, 24, 33);

    /// <summary>格位激活底色（低饱和主色）。</summary>
    public static readonly Color CapsuleActive = Color.FromArgb(30, 94, 143, 246);

    /// <summary>胶囊柔和投影的浓度与扩散（供 DrawSoftShadow 复用）。</summary>
    public const int CapsuleShadowAlpha = 26;
    public const int CapsuleShadowSpread = 7;

    /// <summary>菜单项高度与左右内边距。</summary>
    public const int MenuItemHeight = 32;
    public const int MenuPadX = 8;

    public const int RadiusMedium = 10;
    public const int RadiusLarge = 14;

    /// <summary>按比例淡出一个颜色（保留原色相，仅降不透明度）。</summary>
    public static Color Fade(Color c, float alpha)
        => Color.FromArgb((int)Math.Round(255 * Math.Clamp(alpha, 0f, 1f)), c);

    /// <summary>
    /// 字体一律静态缓存。GDI+ 的 Font 构造开销较大，
    /// 若每次绘制都新建（旧实现是属性 getter），高频重绘时会明显掉帧。
    ///
    /// 注意：这些实例是**共享**的，调用方不要 Dispose。
    /// </summary>
    public static readonly Font UiFont = new("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font UiFontBold = new("Microsoft YaHei UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font SmallFont = new("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>圆角条/功能条这类"手绘胶囊"里的正文：比 SmallFont 大一档，34px 高才不显挤。</summary>
    public static readonly Font ToolbarFont = new("Microsoft YaHei UI", 9.25f, FontStyle.Regular, GraphicsUnit.Point);

    public static readonly Font MonoFont = new("Consolas", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

    public static Font Font(float size, FontStyle style = FontStyle.Regular)
        => new("Microsoft YaHei UI", size, style, GraphicsUnit.Point);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        float d = radius * 2f;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color fill, Color? border = null)
    {
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (border.HasValue)
        {
            using var pen = new Pen(border.Value);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>柔和投影：多层低透明度描边模拟弥散阴影。</summary>
    public static void DrawSoftShadow(Graphics g, RectangleF r, float radius, int spread = 10, int alpha = 26)
    {
        // 复用同一个 Pen，仅改颜色，避免每层新建（高频重绘时分配量可观）
        using var pen = new Pen(Color.Black);
        // spread 非法时直接不画，避免除零；所有既有调用都传正值
        if (spread <= 0) return;
        int step = Math.Max(1, alpha / spread);

        for (int i = spread; i >= 1; i--)
        {
            var rr = RectangleF.Inflate(r, i, i);
            using var path = RoundedRect(rr, radius + i);
            pen.Color = Color.FromArgb(step, 15, 22, 38);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// 绘制"浮层卡片"（提示条、设置提示、结果面板等）。
    ///
    /// 必须先 Clear 再画：这些窗体都重写了空的 OnPaintBackground，
    /// 双缓冲下未绘制区域会保留**未初始化缓冲**，表现为四角出现彩色噪点（边角杂色）。
    /// 阴影也不画在窗口内部——内部阴影会在圆角外露出灰边，看起来像脏污。
    /// </summary>
    public static void PaintCard(Graphics g, Size size, float radius)
    {
        g.Clear(Surface);

        var rect = new RectangleF(0, 0, size.Width, size.Height);
        using var path = RoundedRect(rect, radius);
        using var brush = new SolidBrush(Surface);
        g.FillPath(brush, path);

        using var pen = new Pen(Border);
        g.DrawPath(pen, path);
    }

    /// <summary>把窗体裁成圆角矩形，四角不参与绘制，避免露出底色方块。</summary>
    public static void ApplyRoundedRegion(Form form, float radius)
    {
        using var path = RoundedRect(new RectangleF(0, 0, form.Width, form.Height), radius);
        var old = form.Region;
        form.Region = new Region(path);
        old?.Dispose();
    }

    public static void SetupQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}
