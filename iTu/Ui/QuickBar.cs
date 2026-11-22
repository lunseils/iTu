using System.Drawing.Drawing2D;

namespace iTu.Ui;

/// <summary>
/// 截图态顶部居中的功能条（对照参考图二）：左侧六点拖动柄，然后是
/// 「截图 / 录屏 / 长截图」，一条竖分隔线，右边是「○ 隐藏当前窗口」。
///
/// 和胶囊、次级胶囊、颜色弹层一样，它是**纯手绘对象**，画进 <see cref="OverlayForm"/>
/// 的同一张分层位图，命中也由 <see cref="OverlaySession"/> 统一分发 —— 没有独立窗口，
/// 所以不会在截图里留下"多余的窗口"，也不需要额外处理焦点。
///
/// 显隐规则（由会话决定）：鼠标框选期间、以及选区存在时自动隐藏，
/// 避免它压住待截取的画面。
/// </summary>
public sealed class QuickBar
{
    /// <summary>整条高度（参考图二实测 46px）。</summary>
    public const int Height = 46;

    /// <summary>「截图」的下拉菜单项（顺序即绘制顺序）。</summary>
    public enum Menu { Region = 0, Window = 1, Fullscreen = 2 }

    public enum Btn
    {
        None = 0,
        Capture,
        Record,
        LongShot,

        /// <summary>「隐藏当前窗口」前面的圆圈 —— 现在它是**选择框**：点它只隐藏功能条自己。</summary>
        HideBar
    }

    private const int PadX = 14;
    private const int BtnH = 32;
    private const int BtnPadX = 14;
    private const int Gap = 6;
    private const int DotW = 8;
    private const int DotH = 14;
    private const int CheckD = 16;

    // 下拉菜单
    private const int MenuW = 172;
    private const int MenuItemH = 36;
    private const int MenuPadY = 6;
    private const int MenuGap = 8;

    private static readonly Font TextFont =
        new("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);

    private static readonly Font MenuFont =
        new("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

    private readonly List<(Btn Btn, Rectangle Rect)> _hits = new();

    private Rectangle _dots;
    private Rectangle _divider;
    private Rectangle _check;
    private Rectangle _labelRect;
    private Rectangle _captureRect;

    private Btn _hover = Btn.None;
    private bool _hoverDots;
    private int _hoverMenu = -1;

    /// <summary>条下方的一行状态提示（"长截图暂未支持" 等）。</summary>
    private string? _status;
    private long _statusUntil;

    /// <summary>需要重绘。</summary>
    public event Action? Changed;

    private Size _size;
    private Rectangle _screen = new(0, 0, 1920, 1080);

    public QuickBar()
    {
        Measure();
        Bounds = new Rectangle(0, 0, _size.Width, _size.Height);
    }

    public Rectangle Bounds { get; private set; }

    public bool Visible { get; set; }

    /// <summary>
    /// 被「隐藏当前窗口」前的圆圈收起：整条不画，只留一个 30×20 的六点小柄，
    /// 点它再把条展开（否则点一下就没法恢复，只能重开一次截图）。
    /// </summary>
    public bool Collapsed { get; set; }

    /// <summary>「截图」的下拉菜单是否展开。</summary>
    public bool MenuOpen { get; set; }

    /// <summary>菜单里当前生效的一项（画勾用）。</summary>
    public Menu CurrentMode { get; set; } = Menu.Region;

    /// <summary>鼠标是否按在拖动柄上（拖动期间光标显示为移动）。</summary>
    public bool HoverDots => _hoverDots;

    // ---------------- 排版 ----------------

    private void Measure()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);

        var labels = new (Btn Btn, string Text)[]
        {
            (Btn.Capture, "截图"),
            (Btn.Record, "录屏"),
            (Btn.LongShot, "长截图"),
        };

        int x = PadX;
        int y = (Height - BtnH) / 2;

        _dots = new Rectangle(x, (Height - DotH) / 2, DotW, DotH);
        x += DotW + 12;

        _hits.Clear();
        foreach (var (btn, text) in labels)
        {
            int w = TextWidth(g, text) + BtnPadX * 2;
            var r = new Rectangle(x, y, w, BtnH);
            _hits.Add((btn, r));
            if (btn == Btn.Capture) _captureRect = r;
            x += w + Gap;
        }

        // 竖分隔线
        _divider = new Rectangle(x + 4, y + 3, 1, BtnH - 6);
        x += 4 + 1 + 12;

        // 圆圈（选择框）：只有它自己可点，文字不再参与命中（需求：文字不是选择框）
        _check = new Rectangle(x, (Height - CheckD) / 2, CheckD, CheckD);

        int labelX = x + CheckD + 9;
        int labelW = TextWidth(g, "隐藏当前窗口") + 8;
        _labelRect = new Rectangle(labelX, y, labelW, BtnH);

        // 标签右侧留 14，跟左侧对称
        _size = new Size(labelX + labelW + PadX, Height);
    }

    private static int TextWidth(Graphics g, string text) =>
        (int)Math.Ceiling(g.MeasureString(text, TextFont, int.MaxValue, MeasureFmt).Width);

    private static readonly StringFormat MeasureFmt = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.None
    };

    private static readonly StringFormat Center = new(StringFormatFlags.NoWrap)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center
    };

    private static readonly StringFormat Near = new(StringFormatFlags.NoWrap)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center
    };

    // ---------------- 几何 ----------------

    /// <summary>菜单卡片（屏幕坐标）。条不可见或菜单没开时为空。</summary>
    public Rectangle MenuBounds
    {
        get
        {
            if (!MenuOpen || Collapsed || !Visible) return Rectangle.Empty;

            int h = MenuPadY * 2 + MenuItemH * 3;
            int x = Bounds.Left + _captureRect.Left;
            int y = Bounds.Bottom + MenuGap;

            x = Math.Clamp(x, _screen.Left + 4,
                Math.Max(_screen.Left + 4, _screen.Right - MenuW - 4));
            if (y + h > _screen.Bottom - 4) y = Math.Max(_screen.Top + 4, Bounds.Top - MenuGap - h);
            return new Rectangle(x, y, MenuW, h);
        }
    }

    /// <summary>收起后的小柄（屏幕坐标）。</summary>
    public Rectangle ChipBounds => new(Bounds.Left + 2, Bounds.Top + (Height - 22) / 2, 34, 22);

    /// <summary>「截图」按钮的屏幕矩形（真机脚本用它点开下拉菜单）。</summary>
    public Rectangle CaptureButtonBounds => Offset(_captureRect);

    /// <summary>圆圈（选择框）的屏幕矩形；标签不在命中区内。</summary>
    public Rectangle CheckBounds => Offset(_check);

    /// <summary>整条 + 菜单 + 收起小柄，供脏区计算。</summary>
    public Rectangle DirtyBounds
    {
        get
        {
            if (!Visible) return Rectangle.Empty;
            var r = Collapsed ? ChipBounds : Bounds;
            var menu = MenuBounds;
            return menu.IsEmpty ? r : Rectangle.Union(r, menu);
        }
    }

    /// <summary>放到屏幕顶部居中（限制在给定屏幕内）。</summary>
    public void PlaceTopCenter(Rectangle screen)
    {
        _screen = screen;
        int x = screen.Left + (screen.Width - _size.Width) / 2;
        int y = screen.Top + 10;
        SetPos(new Point(x, y), screen);
    }

    /// <summary>拖动：按新的左上角放置，夹在屏幕内。</summary>
    public void MoveTo(Point topLeft, Rectangle screen)
    {
        SetPos(topLeft, screen);
        Changed?.Invoke();
    }

    private void SetPos(Point p, Rectangle screen)
    {
        _screen = screen;
        int x = Math.Clamp(p.X, screen.Left + 4,
            Math.Max(screen.Left + 4, screen.Right - _size.Width - 4));
        int y = Math.Clamp(p.Y, screen.Top + 4,
            Math.Max(screen.Top + 4, screen.Bottom - _size.Height - 4));
        Bounds = new Rectangle(x, y, _size.Width, _size.Height);
    }

    // ---------------- 命中 ----------------

    /// <summary>屏幕坐标 → 功能格；-1 = 条上非按钮区域（可拖动）；-2 = 条外。</summary>
    public int HitTest(Point screen)
    {
        if (!Visible || Collapsed || !Bounds.Contains(screen)) return -2;

        foreach (var (btn, rect) in _hits)
            if (Offset(rect).Contains(screen)) return (int)btn;

        // 圆圈本身就是选择框：只认圆圈（含 4px 容错），标签不参与命中
        var check = Offset(_check);
        check.Inflate(4, 4);
        if (check.Contains(screen)) return (int)Btn.HideBar;

        return -1;
    }

    public Btn ButtonAt(Point screen)
    {
        int hit = HitTest(screen);
        return hit >= 0 ? (Btn)hit : Btn.None;
    }

    public bool Contains(Point screen) =>
        Visible && !Collapsed && Bounds.Contains(screen);

    /// <summary>点是否落在收起后的小柄上。</summary>
    public bool ChipContains(Point screen) =>
        Visible && Collapsed && ChipBounds.Contains(screen);

    /// <summary>菜单项命中：0..2，-1 未命中。</summary>
    public int MenuItemAt(Point screen)
    {
        var menu = MenuBounds;
        if (menu.IsEmpty || !menu.Contains(screen)) return -1;

        int idx = (screen.Y - menu.Top - MenuPadY) / MenuItemH;
        return idx is >= 0 and < 3 ? idx : -1;
    }

    public bool OnDragHandle(Point screen)
    {
        if (!Visible || Collapsed || !Bounds.Contains(screen)) return false;
        var dots = Offset(_dots);
        dots.Inflate(8, 6);
        return dots.Contains(screen);
    }

    /// <summary>格位矩形（相对整条左上角）→ 屏幕矩形。</summary>
    private Rectangle Offset(Rectangle local) =>
        new(Bounds.Left + local.X, Bounds.Top + local.Y, local.Width, local.Height);

    public void PointerMove(Point screen)
    {
        var btn = ButtonAt(screen);
        bool dots = OnDragHandle(screen);
        int menu = MenuItemAt(screen);
        if (btn == _hover && dots == _hoverDots && menu == _hoverMenu) return;
        _hover = btn;
        _hoverDots = dots;
        _hoverMenu = menu;
        Changed?.Invoke();
    }

    public void PointerLeave()
    {
        if (_hover == Btn.None && !_hoverDots && _hoverMenu < 0) return;
        _hover = Btn.None;
        _hoverDots = false;
        _hoverMenu = -1;
        Changed?.Invoke();
    }

    /// <summary>条下方贴一行状态（几秒后自动消失）。</summary>
    public void SetStatus(string text, int holdMs)
    {
        _status = text;
        _statusUntil = Environment.TickCount64 + Math.Max(600, holdMs);
        Changed?.Invoke();
    }

    public void ClearStatusIfExpired()
    {
        if (_status == null) return;
        if (Environment.TickCount64 < _statusUntil) return;
        _status = null;
        Changed?.Invoke();
    }

    public string? Status => _status;

    // ---------------- 绘制 ----------------

    public void Draw(Graphics g, PointF origin)
    {
        if (!Visible || Bounds.Width < 40) return;

        var state = g.Save();
        g.TranslateTransform(-origin.X, -origin.Y);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (Collapsed)
        {
            DrawChip(g);
            g.Restore(state);
            return;
        }

        var card = new RectangleF(Bounds.X + 0.5f, Bounds.Y + 0.5f, Bounds.Width - 1f, Bounds.Height - 1f);
        Theme.DrawSoftShadow(g, card, 12f, 8, 30);
        Theme.FillRounded(g, card, 12f, Theme.Surface, Theme.Border);

        // 六点拖动柄：2 列 × 3 行
        using (var dotBrush = new SolidBrush(Color.FromArgb(165, 155, 164, 178)))
        {
            var d = Offset(_dots);
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 2; col++)
                    g.FillEllipse(dotBrush,
                        d.Left + col * 4, d.Top + row * 5.2f, 2.2f, 2.2f);
        }

        using var text = new SolidBrush(Theme.Text);
        {
            foreach (var (btn, rect) in _hits)
            {
                var r = Offset(rect);
                bool active = btn == Btn.Capture;
                bool hover = _hover == btn;

                if (active)
                {
                    var box = new RectangleF(r.X, r.Y, r.Width, r.Height);
                    using var path = Theme.RoundedRect(box, 8f);
                    using var fill = new SolidBrush(hover ? Color.FromArgb(246, 249, 255) : Color.FromArgb(250, 251, 253));
                    g.FillPath(fill, path);
                    using var pen = new Pen(hover ? Theme.Accent : Color.FromArgb(230, 233, 238), 1f);
                    g.DrawPath(pen, path);
                }
                else if (hover)
                {
                    var box = new RectangleF(r.X, r.Y, r.Width, r.Height);
                    Theme.FillRounded(g, box, 8f, Theme.SurfaceHover);
                }

                text.Color = (active || hover) ? Theme.Accent : Theme.Text;
                g.DrawString(LabelOf(btn), TextFont, text,
                    new RectangleF(r.X, r.Y, r.Width, r.Height), Center);
            }

            // 圆圈（选择框）+ 标签（标签不参与命中，只做说明）
            var c = Offset(_check);
            bool chkHover = _hover == Btn.HideBar;
            if (Collapsed)
            {
                using var fill = new SolidBrush(Theme.Accent);
                g.FillEllipse(fill, c.X, c.Y, c.Width, c.Height);
                DrawTick(g, c);
            }
            else
            {
                using var ring = new Pen(chkHover ? Theme.Accent : Color.FromArgb(198, 204, 213), 1.6f);
                g.DrawEllipse(ring, c.X + 0.8f, c.Y + 0.8f, c.Width - 1.6f, c.Height - 1.6f);
            }

            text.Color = chkHover ? Theme.Accent : Theme.Text;
            var lr = Offset(_labelRect);
            g.DrawString("隐藏当前窗口", TextFont, text,
                new RectangleF(lr.X, lr.Y, lr.Width, lr.Height), Near);

            // 竖分隔线
            var dv = Offset(_divider);
            using (var pen = new Pen(Color.FromArgb(230, 233, 238), 1f))
                g.DrawLine(pen, dv.Left, dv.Top, dv.Left, dv.Bottom);
        }

        // 状态提示：贴在条下方，深色小药丸
        if (_status != null)
            DrawStatus(g);

        // 下拉菜单画在最上层（会盖住状态提示，这是期望的）
        if (MenuOpen)
            DrawMenu(g);

        g.Restore(state);
    }

    /// <summary>收起态：一个小胶囊 + 六点，点它展开。</summary>
    private void DrawChip(Graphics g)
    {
        var chip = ChipBounds;
        var r = new RectangleF(chip.X + 0.5f, chip.Y + 0.5f, chip.Width - 1, chip.Height - 1);
        bool hover = _hoverDots || _hover == Btn.HideBar;
        Theme.DrawSoftShadow(g, r, 10f, 6, 24);
        Theme.FillRounded(g, r, 10f, Theme.Surface, hover ? Theme.Accent : Theme.Border);

        using var dotBrush = new SolidBrush(Color.FromArgb(190, 150, 160, 175));
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 2; col++)
                g.FillEllipse(dotBrush,
                    r.X + 13 + col * 4, r.Y + 5 + row * 4.4f, 2.2f, 2.2f);
    }

    private static void DrawTick(Graphics g, Rectangle box)
    {
        using var tick = new Pen(Color.White, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        float cx = box.Left, cy = box.Top;
        g.DrawLines(tick, new[]
        {
            new PointF(cx + 4.0f, cy + 8.2f),
            new PointF(cx + 7.0f, cy + 11.0f),
            new PointF(cx + 12.0f, cy + 5.2f)
        });
    }

    private void DrawMenu(Graphics g)
    {
        var menu = MenuBounds;
        var card = new RectangleF(menu.X + 0.5f, menu.Y + 0.5f, menu.Width - 1, menu.Height - 1);
        Theme.DrawSoftShadow(g, card, 12f, 8, 30);
        Theme.FillRounded(g, card, 12f, Theme.Surface, Theme.Border);

        using var brush = new SolidBrush(Theme.Text);
        for (int i = 0; i < 3; i++)
        {
            var item = new Rectangle(menu.X + 5, menu.Y + MenuPadY + i * MenuItemH,
                menu.Width - 10, MenuItemH);
            bool on = i == (int)CurrentMode;
            bool hover = i == _hoverMenu;

            if (hover)
                Theme.FillRounded(g, new RectangleF(item.X, item.Y, item.Width, item.Height),
                    8f, Theme.SurfaceHover);

            brush.Color = on || hover ? Theme.Accent : Theme.Text;
            g.DrawString(MenuLabel(i), MenuFont, brush,
                new RectangleF(item.X + 32, item.Y, item.Width - 32, item.Height), Near);

            // 勾选态：左侧画一个对勾，和圆角条上的勾选框同一套视觉
            if (on)
            {
                using var tick = new Pen(Theme.Accent, 1.9f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                float cx = item.X + 10, cy = item.Y + item.Height / 2f - 5;
                g.DrawLines(tick, new[]
                {
                    new PointF(cx, cy + 4.6f),
                    new PointF(cx + 3f, cy + 7.6f),
                    new PointF(cx + 8.4f, cy + 1.6f)
                });
            }
        }
    }

    public static string MenuLabel(int index) => index switch
    {
        0 => "自由截图",
        1 => "窗口截图",
        2 => "全屏截图",
        _ => ""
    };

    private static string LabelOf(Btn btn) => btn switch
    {
        Btn.Capture => "截图",
        Btn.Record => "录屏",
        Btn.LongShot => "长截图",
        _ => ""
    };

    private void DrawStatus(Graphics g)
    {
        var font = Theme.SmallFont;
        var size = g.MeasureString(_status, font);
        var box = new RectangleF(
            Bounds.Left + (Bounds.Width - (size.Width + 20)) / 2f,
            Bounds.Bottom + 8, size.Width + 20, 26);

        using var path = Theme.RoundedRect(box, 13f);
        using (var fill = new SolidBrush(Color.FromArgb(226, 30, 34, 42)))
            g.FillPath(fill, path);
        using var brush = new SolidBrush(Color.White);
        g.DrawString(_status, font, brush, box.X + 10, box.Y + 5);
    }
}
