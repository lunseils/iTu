using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using iTu.Core;

namespace iTu.Ui;

/// <summary>
/// 胶囊风格的弹出菜单。
///
/// 为什么不用 <see cref="ContextMenuStrip"/>：系统菜单是方角、灰底、自带系统投影，
/// 和胶囊的白面圆角完全不搭。这里用分层窗口自绘：同一套圆角、描边、柔影与图标语言。
///
/// 交互：点外部或按 Esc 关闭；鼠标移入高亮；单击执行。
/// </summary>
public sealed class CapsuleMenu : Form
{
    public sealed record MenuItem(string Text, IconKind? Icon, Action? Action)
    {
        public static MenuItem Separator => new("-", null, null);
        public bool IsSeparator => Text == "-";
    }

    private const int Width0 = 196;
    private const int PadY = 6;
    private const int IconSize = 16;
    private const int ShadowPad = 10;

    private readonly List<MenuItem> _items;
    private int _hover = -1;

    /// <summary>刚弹出的一小段时间内不要因为"点击外部"就立刻关闭（托盘点击的余波）。</summary>
    private long _graceUntil;

    private CapsuleMenu(IEnumerable<MenuItem> items)
    {
        _items = items.Where(i => i != null).ToList();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Surface;
        AutoScaleMode = AutoScaleMode.None;

        int h = PadY * 2;
        foreach (var it in _items)
            h += it.IsSeparator ? 9 : Theme.MenuItemHeight;

        _size = new Size(Width0 + ShadowPad * 2, h + ShadowPad * 2);
        ClientSize = _size;
    }

    private readonly Size _size;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED：圆角与柔影需要逐像素 alpha
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>
    /// 在指定屏幕位置弹出菜单。菜单是非阻塞的，返回的是菜单实例，
    /// 调用方可订阅 FormClosed 做资源回收。
    /// </summary>
    public static CapsuleMenu Popup(Point screenPos, IEnumerable<MenuItem> items)
    {
        var menu = new CapsuleMenu(items);

        // 夹到工作区内，别贴边被截掉
        var area = Screen.FromPoint(screenPos).WorkingArea;
        var size = menu._size;                       // 取副本：直接访问字段成员会被 ref 封送
        // 极小工作区下 max 可能小于 min，Math.Clamp 会抛异常
        int minX = area.Left + 2, maxX = Math.Max(minX, area.Right - size.Width - 2);
        int minY = area.Top + 2, maxY = Math.Max(minY, area.Bottom - size.Height - 2);
        int x = Math.Clamp(screenPos.X - ShadowPad, minX, maxX);
        int y = Math.Clamp(screenPos.Y - ShadowPad, minY, maxY);
        menu.Location = new Point(x, y);

        menu.Show();
        menu.Render();

        // 从托盘图标弹菜单时，我们的进程往往不是前台窗口：
        // 不抢一次前台的话，窗体永远不会"激活"，也就永远收不到 Deactivate，
        // 菜单会一直挂在那里（用户反馈的"不会自动隐藏"）。
        menu.Activate();
        Native.SetForegroundWindow(menu.Handle);

        // 捕获鼠标：点在任何其它窗口上都会先送到我们这里，据此关闭菜单，
        // 不依赖"激活/失焦"这套在托盘场景下不可靠的机制。
        menu.Capture = true;
        menu._graceUntil = Environment.TickCount64 + 250;
        return menu;
    }

    private void Render()
    {
        if (!IsHandleCreated) return;

        using var bmp = LayeredSurface.Supersample(_size, g =>
        {
            Theme.SetupQuality(g);

            var card = new RectangleF(ShadowPad, ShadowPad,
                _size.Width - ShadowPad * 2, _size.Height - ShadowPad * 2);

            Theme.DrawSoftShadow(g, card, Theme.RadiusLarge, 9, 34);
            Theme.FillRounded(g, card, Theme.RadiusLarge, Theme.Surface, Theme.Border);

            // 背景"图片"：裁剪在圆角卡片内，菜单才有质感而不是一块死白
            using (var clip = Theme.RoundedRect(card, Theme.RadiusLarge))
            {
                var mode = g.Save();
                g.SetClip(clip);
                g.DrawImage(MenuTexture.For(new Size((int)card.Width, (int)card.Height)),
                    0, 0, card.Width, card.Height);
                g.Restore(mode);
            }

            int y = ShadowPad + PadY;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];

                if (item.IsSeparator)
                {
                    using var pen = new Pen(Theme.Border, 1f);
                    g.DrawLine(pen, card.Left + 10, y + 4, card.Right - 10, y + 4);
                    y += 9;
                    continue;
                }

                var row = new RectangleF(card.Left + Theme.MenuPadX, y,
                    card.Width - Theme.MenuPadX * 2, Theme.MenuItemHeight);

                if (i == _hover)
                {
                    using var path = Theme.RoundedRect(row, 7);
                    using var brush = new SolidBrush(Theme.CapsuleHover);
                    g.FillPath(brush, path);
                }

                var textColor = i == _hover ? Theme.Accent : Theme.Text;
                float textX = row.Left + 10;

                if (item.Icon is { } kind)
                {
                    Icons.Draw(g, kind,
                        new RectangleF(row.Left + 8, row.Top + (Theme.MenuItemHeight - IconSize) / 2f, IconSize, IconSize),
                        i == _hover ? Theme.Accent : Theme.Icon);
                    textX += IconSize + 10;
                }

                using var font = Theme.Font(9f);
                using var tb = new SolidBrush(textColor);
                using var fmt = new StringFormat { LineAlignment = StringAlignment.Center };
                g.DrawString(item.Text, font, tb, new RectangleF(textX, row.Top, row.Right - textX - 8, row.Height), fmt);

                y += Theme.MenuItemHeight;
            }
        });

        LayeredSurface.Submit(Handle, bmp, Location);
    }

    /// <summary>鼠标位置 → 菜单项序号（跳过分隔线）。</summary>
    private int HitTest(Point p)
    {
        int y = ShadowPad + PadY;
        for (int i = 0; i < _items.Count; i++)
        {
            int h = _items[i].IsSeparator ? 9 : Theme.MenuItemHeight;
            if (!_items[i].IsSeparator && p.Y >= y && p.Y < y + h &&
                p.X >= ShadowPad && p.X <= _size.Width - ShadowPad)
                return i;
            y += h;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Render();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Render(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        // 命中窗口之外（开启了 Capture，这类点击也会送到这里）→ 关闭。
        // 刚弹出的 250ms 属于托盘点击余波，忽略，避免"刚弹出就消失"。
        if (Environment.TickCount64 < _graceUntil) return;

        if (e.X < 0 || e.Y < 0 || e.X >= _size.Width || e.Y >= _size.Height)
        {
            Close();
            return;
        }

        int hit = HitTest(e.Location);
        if (hit < 0) { Close(); return; }

        var action = _items[hit].Action;
        Close();
        action?.Invoke();
    }

    private System.Windows.Forms.Timer? _watch;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 兜底守卫：即使拿不到前台/收不到鼠标消息（某些托盘场景），
        // 只要前台窗口已经不是自己，就自行关闭，绝不留在屏幕上。
        _watch = new System.Windows.Forms.Timer { Interval = 200 };
        _watch.Tick += (_, _) =>
        {
            if (IsDisposed || !Visible) return;
            if (Environment.TickCount64 < _graceUntil) return;

            var fg = GetForegroundWindow();
            if (fg != Handle) Close();
        };
        _watch.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Environment.TickCount64 >= _graceUntil) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watch?.Stop();
        _watch?.Dispose();
        _watch = null;
        base.OnFormClosed(e);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }
}
