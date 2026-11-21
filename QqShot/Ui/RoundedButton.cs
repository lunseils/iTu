using System.Drawing.Drawing2D;

namespace QqShot.Ui;

/// <summary>
/// 胶囊风格的圆角按钮。
///
/// 系统 Button 是直角矩形，和胶囊的圆角语言冲突；这里自绘：
/// 圆角 8、主色/幽灵两种形态、悬停提亮、按下压暗，与胶囊图标的反馈节奏一致。
/// </summary>
public sealed class RoundedButton : Control
{
    public enum Kind
    {
        /// <summary>主操作：主色填充 + 白字。</summary>
        Primary,
        /// <summary>次要操作：浅底 + 描边 + 次要文字。</summary>
        Ghost
    }

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.UiFont;
        Cursor = Cursors.Hand;
        Size = new Size(88, 32);
    }

    /// <summary>形态。</summary>
    public Kind Style { get; set; } = Kind.Ghost;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => base.Text;
        set { base.Text = value; Invalidate(); }
    }

    private bool _hover, _pressed;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Surface);

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundedRect(r, 8);

        if (Style == Kind.Primary)
        {
            var fill = _pressed ? Theme.Fade(Theme.Accent, 0.82f)
                     : _hover ? Theme.Fade(Theme.Accent, 0.92f)
                     : Theme.Accent;
            using var brush = new SolidBrush(Enabled ? fill : Theme.Fade(Theme.Accent, 0.35f));
            g.FillPath(brush, path);
        }
        else
        {
            using (var brush = new SolidBrush(_pressed ? Theme.SurfaceHover : _hover ? Theme.SurfaceHover : Theme.SurfaceAlt))
                g.FillPath(brush, path);
            using var pen = new Pen(_hover ? Theme.BorderStrong : Theme.Border, 1f);
            g.DrawPath(pen, path);
        }

        var color = !Enabled
            ? Theme.Fade(Theme.TextSecondary, 0.5f)
            : Style == Kind.Primary ? Theme.TextOnAccent
            : _hover ? Theme.Accent : Theme.TextSecondary;

        using var tb = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, tb, r, fmt);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _pressed = false; Invalidate(); }

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
