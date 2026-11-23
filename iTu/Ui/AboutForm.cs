using System.Diagnostics;
using iTu.Core;

namespace iTu.Ui;

/// <summary>
/// 「关于 / 诊断」对话框：版本、进程、路径、热键与日志状态，
/// 页面最底部是一条可点击跳转的 GitHub 项目链接。
/// 视觉语言与设置面板一致（无边框 + 自绘标题栏 + Theme 配色）。
/// </summary>
public sealed class AboutForm : Form
{
    private const int TitleHeight = 46;
    private const int FooterHeight = 54;

    private bool _dragging;
    private Point _dragOrigin;
    private Point _windowOrigin;

    public AboutForm(string diagnostics)
    {
        FormBorderStyle = FormBorderStyle.None;   // 与设置面板一致的无边框窗口
        StartPosition = FormStartPosition.CenterScreen;  // 托盘程序无主窗，CenterParent 会退化到左上角
        AutoScaleMode = AutoScaleMode.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        ClientSize = new Size(560, 400);
        Font = Theme.UiFont;
        DoubleBuffered = true;
        KeyPreview = true;

        // ---- 诊断文本（只读，可选中复制）----
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Padding = new Padding(24, TitleHeight + 10, 24, 8)
        };
        var diagBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.SmallFont,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Text = diagnostics
        };
        body.Controls.Add(diagBox);
        Controls.Add(body);

        // ---- 底部：分隔线 + GitHub 跳转链接 ----
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = FooterHeight,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 0, 20, 0)
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        var prefix = "GitHub 项目地址：";
        var url = AppInfo.GitHubUrl;
        var link = new LinkLabel
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.UiFont,
            Text = prefix + url,
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.Danger,
            VisitedLinkColor = Theme.TextSecondary,
            BackColor = Theme.Surface
        };
        link.Links.Clear();
        link.Links.Add(prefix.Length, url.Length, url);
        link.LinkClicked += (_, e) =>
        {
            var target = e.Link?.LinkData as string ?? url;
            OpenUrl(target);
        };
        footer.Controls.Add(link);
        Controls.Add(footer);

        // ---- 自绘标题栏的关闭按钮 ----
        var close = new IconButton
        {
            Icon = IconKind.Close,
            Hint = "关闭",
            Bounds = new Rectangle(ClientSize.Width - 46, 11, 30, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        close.BringToFront();

        Theme.ApplyRoundedRegion(this, 16);
    }

    /// <summary>用系统默认浏览器打开链接。</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            // 当前来源是常量 AppInfo.GitHubUrl，安全；仍校验 scheme，防止将来
            // 改成可配置值时被塞入 file:// / javascript: 等危险协议。
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Log.Info("[about] 拒绝打开非 http(s) 链接: " + url);
                return;
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开链接：" + ex.Message, "iTu",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>背景卡片 + 标题栏文字，与设置面板同一套视觉。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        Theme.SetupQuality(g);

        var card = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
        using (var path = Theme.RoundedRect(RectangleF.Inflate(card, -0.5f, -0.5f), 16))
        using (var pen = new Pen(Theme.Border, 1f))
            g.DrawPath(pen, path);

        using (var brush = new SolidBrush(Theme.Text))
            g.DrawString("关于 / 诊断", Theme.Font(11f, FontStyle.Bold), brush, 26, 14);

        base.OnPaint(e);
    }

    /// <summary>按住标题栏（左上角非控件区域）拖动窗口。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.Y > TitleHeight) return;

        _dragging = true;
        _dragOrigin = Cursor.Position;
        _windowOrigin = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        var now = Cursor.Position;
        Location = new Point(_windowOrigin.X + (now.X - _dragOrigin.X),
                             _windowOrigin.Y + (now.Y - _dragOrigin.Y));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    /// <summary>Win11 圆角：系统给无边框窗口裁出真正的抗锯齿圆角。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int pref = 2;   // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch
        {
            // Win10 不支持：退化成直角，Region 已保证圆角，不影响观感
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
