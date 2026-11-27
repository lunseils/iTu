using System.Drawing.Drawing2D;
using iTu.Core;

namespace iTu.Ui;

/// <summary>
/// 悬停提示（工具名）。
///
/// 为什么不用标准 <see cref="ToolTip"/>：胶囊和调节条都是 WS_EX_NOACTIVATE 的分层窗口，
/// 标准提示在这些窗口上不会显示。
///
/// 为什么用分层窗口自绘，而不是"实心窗体 + Region 裁圆角"：
/// Region 的圆角是位图掩码，边缘必然是硬像素（1px 台阶），小圆角尤其明显。
/// 分层窗口支持逐像素 alpha，配合超采样绘制，圆角能画出真正的抗锯齿过渡。
/// </summary>
public sealed class HintTip : Form
{
    private const int Radius = 8;      // 与胶囊的圆角语言一致
    private const int PadX = 7;
    private const int PadY = 4;
    private const int ShadowPad = 6;   // 画布上留给柔影的透明边

    private static HintTip? _current;
    private string _text = "";
    private Size _size;

    private HintTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        FormClosed += (_, _) => { if (ReferenceEquals(_current, this)) _current = null; };
    }

    /// <summary>是否有可见提示。编辑器据此暂停"重申置顶"，否则会把提示压在下面。</summary>
    public static bool AnyVisible => _current is { IsDisposed: false, Visible: true };

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED;   // 逐像素 alpha（圆角 + 柔影）
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab
            cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT：鼠标穿透（提示不该吃掉滚轮/点击）
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override bool ShowWithoutActivation => true;

    /// <summary>在锚点正上方显示（尺寸量好再上移，标签底边与锚点固定留 4px）。</summary>
    public static void ShowTipAbove(string text, Point bottomCenter)
    {
        var tip = Ensure(text);
        if (tip == null) return;

        var size = tip._size;   // 取副本：直接访问字段成员会被 ref 封送
        tip.Location = new Point(bottomCenter.X - size.Width / 2, bottomCenter.Y - size.Height - 2);
        tip.Present();
    }

    /// <summary>在指定屏幕位置显示（左上角对齐）。</summary>
    public static void ShowTip(string text, Point topLeft)
    {
        var tip = Ensure(text);
        if (tip == null) return;

        tip.Location = topLeft;
        tip.Present();
    }

    public static void HideTip()
    {
        if (_current is { IsDisposed: false } t && t.Visible) t.Hide();
    }

    private static HintTip? Ensure(string text)
    {
        _current ??= new HintTip();
        var tip = _current;
        if (tip.IsDisposed) { _current = null; return null; }

        tip.Measure(text);
        return tip;
    }

    private void Present()
    {
        if (!Visible) Show();
        Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Render();
    }

    private void Measure(string text)
    {
        _text = text;
        var t = TextRenderer.MeasureText(text, Theme.SmallFont);
        _size = new Size(t.Width + PadX * 2 + ShadowPad * 2, t.Height + PadY * 2 + ShadowPad * 2);
        ClientSize = _size;
    }

    private void Render()
    {
        if (!IsHandleCreated || _size.Width <= 0) return;

        using var bmp = LayeredSurface.Supersample(_size, g =>
        {
            var card = new RectangleF(ShadowPad, ShadowPad,
                _size.Width - ShadowPad * 2, _size.Height - ShadowPad * 2);

            Theme.DrawSoftShadow(g, card, Radius, 5, 22);
            Theme.FillRounded(g, card, Radius, Theme.Surface, Theme.Border);

            using var brush = new SolidBrush(Theme.Text);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(_text, Theme.SmallFont, brush, card, fmt);
        });

        LayeredSurface.Submit(Handle, bmp, Location);
    }
}
