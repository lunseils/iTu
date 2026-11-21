using System.Drawing.Drawing2D;
using QqShot.Core;

namespace QqShot.Ui;

/// <summary>
/// 第三级：取色弹层（7 色柔和色板 + 「自定义…」）。
///
/// 纯手绘对象，不是窗口 —— 它画进 <see cref="OverlayForm"/> 的同一张分层位图，
/// 命中由 <see cref="OverlaySession"/> 分发。
///
/// 形态：**永远是一条胶囊**，高度取次级胶囊的高度、圆角取高度一半，
/// 与主胶囊 / 次级胶囊同一套语言（曾经试过"贴边时改成三条小胶囊"，
/// 但三级控件本来就是"一条"，排成三行反而多出两行高度、看着像另开了一个面板）。
///
/// 落位顺序（任何情况下都要完整留在屏内、且不与前两级重叠）：
///   右侧同一行 → 左侧同一行 → 下方一条 → 上方一条 → 夹取。
/// </summary>
public sealed class ColorPickerChrome
{
    /// <summary>与胶囊高度保持一致（见 <see cref="PillToolbar.BaseSize"/>）。</summary>
    public const int Height0 = 38;

    private const int ShadowPad = 5;
    private const int PadX = 10;
    private const int Gap = 5;
    private const int ButtonW = 74;
    private const int ButtonGap = 10;

    private readonly Color[] _palette = Theme.ToolPalette;
    private readonly Action<Color> _onPick;
    private readonly Color _current;

    /// <summary>次级胶囊高度：卡片高度就取它（两级等高）。</summary>
    private readonly int _capsuleH;

    /// <summary>色块边长：随胶囊高度缩放，但不小于 16（再小点不中）。</summary>
    private readonly int _swatch;

    private Size _size;
    private Point _pos;

    private int _hoverSwatch = -1;
    private bool _hoverButton;

    public event Action? Changed;
    /// <summary>请求打开系统取色框（由会话弹出，避免抢焦点）。</summary>
    public event Action? CustomColorRequested;

    /// <param name="capsuleHeight">次级胶囊高度；卡片与它等高。</param>
    public ColorPickerChrome(Color current, Action<Color> onPick, int capsuleHeight = Height0)
    {
        _current = current;
        _onPick = onPick;

        _capsuleH = Math.Max(24, capsuleHeight);
        _swatch = Math.Clamp(_capsuleH - 12, 16, 26);

        SetCard(new Rectangle(0, 0, RowCard.Width, RowCard.Height));
    }

    public Rectangle Bounds => new(_pos, _size);

    /// <summary>落位策略（供日志/真机断言）：right / left / below / above / clamp。</summary>
    public string PlaceMode { get; private set; } = "right";

    // ---------------- 尺寸 ----------------

    private int RowWidth => PadX * 2 + _palette.Length * (_swatch + Gap) - Gap + ButtonGap + ButtonW;

    private Size RowCard => new(RowWidth, _capsuleH);

    private void SetCard(Rectangle card)
    {
        _size = new Size(card.Width + ShadowPad * 2, card.Height + ShadowPad * 2);
        _pos = new Point(card.X - ShadowPad, card.Y - ShadowPad);
    }

    /// <summary>
    /// 落位：**优先落在锚点下方、与主胶囊同一列的起点对齐**（三级控件同列），
    /// 下方放不下再试上方、右侧同一行、左侧同一行，最后夹取到屏幕内。
    /// 任何位置都不能与前两级控件重叠。
    /// </summary>
    /// <param name="avoid">锚点控件（次级胶囊；没有时为副胶囊）。</param>
    /// <param name="screen">可用屏幕（虚拟桌面物理像素）。</param>
    /// <param name="blocker">必须避开的同层控件（主胶囊）。</param>
    /// <param name="columnLeft">整列的起点（主胶囊左边缘）；不传则退回锚点左边缘。</param>
    public void Place(Rectangle avoid, Rectangle screen, Rectangle blocker, int columnLeft = int.MinValue)
    {
        const int gap = 8;
        var card = RowCard;
        int col = columnLeft == int.MinValue ? avoid.Left : columnLeft;

        bool Fits(Rectangle r) =>
            r.Left >= screen.Left && r.Top >= screen.Top &&
            r.Right <= screen.Right && r.Bottom <= screen.Bottom &&
            !r.IntersectsWith(blocker) && !r.IntersectsWith(avoid);

        // 列起点对齐：贴右时整条左移（宁可让起点偏一点，也不能把卡片切掉）
        int AlignX(int x) => Math.Min(x, screen.Right - card.Width);

        // 1) 锚点下方一条 —— 与前两级同列、同起点（这就是"三行"的第三行）
        var below = new Rectangle(AlignX(col), avoid.Bottom + gap, card.Width, card.Height);
        if (Fits(below)) { Commit(below, "below"); return; }

        // 2) 锚点上方一条（下方被屏幕底边挡住时）
        var above = new Rectangle(AlignX(col), avoid.Top - gap - card.Height, card.Width, card.Height);
        if (Fits(above)) { Commit(above, "above"); return; }

        int midY = avoid.Top + (avoid.Height - card.Height) / 2;

        // 3) 右侧同一行
        var right = new Rectangle(avoid.Right + gap, midY, card.Width, card.Height);
        if (Fits(right)) { Commit(right, "right"); return; }

        // 4) 左侧同一行
        var left = new Rectangle(avoid.Left - gap - card.Width, midY, card.Width, card.Height);
        if (Fits(left)) { Commit(left, "left"); return; }

        // 5) 兜底：夹到屏幕内（宁可压住一点，也不能看不见）
        var fallback = new Rectangle(
            Math.Clamp(below.X, screen.Left, Math.Max(screen.Left, screen.Right - card.Width)),
            Math.Clamp(below.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - card.Height)),
            card.Width, card.Height);
        Commit(fallback, "clamp");
    }

    private void Commit(Rectangle card, string mode)
    {
        PlaceMode = mode;
        SetCard(card);
        DumpGeometry();
    }

    private void DumpGeometry()
    {
        Log.Info($"[picker] bounds={Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height} " +
                 $"card={Card.X},{Card.Y},{Card.Width},{Card.Height} " +
                 $"button={ButtonRect.X},{ButtonRect.Y},{ButtonRect.Width},{ButtonRect.Height} " +
                 $"swatch={_swatch} count={_palette.Length} " +
                 $"mode=row place={PlaceMode} rows=1 capsuleH={_capsuleH}");
        Changed?.Invoke();
    }

    private Rectangle Card => new(_pos.X + ShadowPad, _pos.Y + ShadowPad,
        _size.Width - ShadowPad * 2, _size.Height - ShadowPad * 2);

    private Rectangle SwatchRect(int i)
    {
        var card = Card;
        int y = card.Top + (card.Height - _swatch) / 2;
        return new Rectangle(card.Left + PadX + i * (_swatch + Gap), y, _swatch, _swatch);
    }

    private Rectangle ButtonRect
    {
        get
        {
            var card = Card;
            return new Rectangle(card.Right - PadX - ButtonW, card.Top + 4, ButtonW, card.Height - 8);
        }
    }

    // ---------------- 绘制 ----------------

    public void Draw(Graphics g, PointF origin)
    {
        var state = g.Save();
        g.TranslateTransform(-origin.X, -origin.Y);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var card = Card;
        var cf = new RectangleF(card.X, card.Y, card.Width, card.Height);
        float radius = card.Height / 2f;
        Theme.DrawSoftShadow(g, cf, radius, 5, 26);
        Theme.FillRounded(g, cf, radius, Theme.Surface, Theme.Border);

        for (int i = 0; i < _palette.Length; i++)
        {
            var r = SwatchRect(i);
            var box = new RectangleF(r.X, r.Y, r.Width, r.Height);
            // 白色/极浅色块在白色卡片上会"消失"，描边需要明显一档
            var edge = _palette[i].GetBrightness() > 0.9f
                ? Theme.BorderStrong
                : Color.FromArgb(26, 0, 0, 0);
            Theme.FillRounded(g, box, 6, _palette[i], edge);

            if (i == _hoverSwatch)
                using (var ring = new Pen(Theme.BorderStrong, 1.3f))
                using (var path = Theme.RoundedRect(RectangleF.Inflate(box, 2, 2), 8))
                    g.DrawPath(ring, path);

            if (_palette[i].ToArgb() == _current.ToArgb())
                using (var ring = new Pen(Theme.Accent, 2f))
                using (var path = Theme.RoundedRect(RectangleF.Inflate(box, 2.5f, 2.5f), 8))
                    g.DrawPath(ring, path);
        }

        var b = ButtonRect;
        var bf = new RectangleF(b.X, b.Y, b.Width, b.Height);
        using (var path = Theme.RoundedRect(bf, 8))
        {
            using var fill = new SolidBrush(_hoverButton ? Theme.SurfaceHover : Theme.SurfaceAlt);
            g.FillPath(fill, path);
            using var pen = new Pen(_hoverButton ? Theme.Accent : Theme.Border, 1f);
            g.DrawPath(pen, path);
        }

        using (var brush = new SolidBrush(_hoverButton ? Theme.Accent : Theme.TextSecondary))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString("自定义…", Theme.SmallFont, brush, bf, fmt);

        g.Restore(state);
    }

    // ---------------- 命中 ----------------

    public bool Contains(Point screen) => Bounds.Contains(screen);

    private int HitSwatch(Point screen)
    {
        for (int i = 0; i < _palette.Length; i++)
            if (SwatchRect(i).Contains(screen)) return i;
        return -1;
    }

    public void PointerMove(Point screen)
    {
        int s = HitSwatch(screen);
        bool onButton = ButtonRect.Contains(screen);
        if (s == _hoverSwatch && onButton == _hoverButton) return;

        _hoverSwatch = s;
        _hoverButton = onButton;
        Changed?.Invoke();
    }

    public void PointerLeave()
    {
        if (_hoverSwatch != -1 || _hoverButton)
        {
            _hoverSwatch = -1;
            _hoverButton = false;
            Changed?.Invoke();
        }
    }

    public void PointerDown(Point screen)
    {
        int s = HitSwatch(screen);
        if (s >= 0)
        {
            _onPick(_palette[s]);
            return;
        }

        if (ButtonRect.Contains(screen)) CustomColorRequested?.Invoke();
    }
}
