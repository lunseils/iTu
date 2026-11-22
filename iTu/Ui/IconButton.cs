using System.Drawing.Drawing2D;

namespace iTu.Ui;

/// <summary>
/// 小图标按钮：悬停给一层淡淡的底色，按下略微下沉，可带危险色（关闭按钮）。
///
/// 只画图标不画文字 —— 胶囊工具条曾用它，现在改为自绘的 <see cref="PillToolbar"/>；
/// 钉图窗口/启动提示里仍用它做窗口级按钮。
/// </summary>
public sealed class IconButton : Control
{
    private bool _hover;
    private bool _pressed;

    /// <summary>按钮图标。</summary>
    public IconKind Icon { get; set; } = IconKind.Close;

    /// <summary>悬停提示文字（为空则不提示）。</summary>
    public string Hint { get; set; } = "";

    /// <summary>危险色：用于关闭这类破坏性按钮。</summary>
    public bool Danger { get; set; }

    /// <summary>图标颜色覆盖；为 null 时按 Danger/常态自动取色。</summary>
    public Color? IconColor { get; set; }

    public IconButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    private Color StrokeColor =>
        IconColor ?? (Danger ? Theme.Danger : (Enabled ? Theme.Text : Theme.TextMuted));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.SetupQuality(g);
        g.Clear(Parent?.BackColor ?? Theme.Surface);

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

        if (_pressed || _hover)
        {
            var fill = _pressed
                ? (Danger ? Color.FromArgb(40, Theme.Danger) : Color.FromArgb(26, 20, 24, 33))
                : (Danger ? Color.FromArgb(22, Theme.Danger) : Color.FromArgb(14, 20, 24, 33));

            using var path = Theme.RoundedRect(rect, 7);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        float pad = Math.Max(4f, Width * 0.2f);
        Icons.Draw(g, Icon,
            new RectangleF(pad, pad, Width - pad * 2, Height - pad * 2),
            StrokeColor);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        if (!string.IsNullOrEmpty(Hint))
        {
            // PointToScreen 的参数是**本控件客户区坐标**，不能再叠加 Left/Top
            var p = PointToScreen(new Point(Width / 2 - 20, -30));
            HintTip.ShowTip(Hint, p);
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        HintTip.HideTip();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }
}
