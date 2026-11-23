using System.Drawing.Drawing2D;
using iTu.Core;

namespace iTu.Ui;

/// <summary>
/// 程序启动时的快捷键提醒面板。
///
/// 列出三个核心快捷键（自由截图 / 全屏截图 / 屏幕录制），置顶显示数秒后自动淡出。
///
/// 与历史版本的差异（关键修复）：
///   旧版这个面板是 TopMost 的普通窗体，用户按快捷键截图时它正好被拍进画面，
///   成了"截图里多出来的窗口"。现在：
///     1. 用 WS_EX_NOACTIVATE + WS_EX_TRANSPARENT 分层窗口，不抢焦点、鼠标穿透；
///     2. 提供 <see cref="CloseHint"/>，截图流程抓屏前调用它立即关闭，
///        保证不会残留到快照里。
/// </summary>
public sealed class StartupHint : Form
{
    private const int Radius = 12;
    private const int PadX = 20;
    private const int PadY = 16;
    private const int ShadowPad = 10;   // 画布上留给柔影的透明边

    private static StartupHint? _current;

    private readonly System.Windows.Forms.Timer _dismiss;

    private StartupHint(AppSettings settings)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;

        _dismiss = new System.Windows.Forms.Timer { Interval = 5000 };
        _dismiss.Tick += (_, _) => Close();

        FormClosed += (_, _) =>
        {
            _dismiss.Stop();
            _dismiss.Dispose();
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    /// <summary>是否有可见的启动提醒。编辑器据此在自顶前避免遮挡判断。</summary>
    public static bool AnyVisible => _current is { IsDisposed: false, Visible: true };

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED：逐像素 alpha（圆角 + 柔影）
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab
            cp.ExStyle |= 0x00000020;   // WS_EX_TRANSPARENT：鼠标穿透
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override bool ShowWithoutActivation => true;

    /// <summary>在指定位置显示启动提醒（左上角对齐，物理像素）。</summary>
    public static void Show(AppSettings settings, Point topLeft)
    {
        CloseHint();

        var hint = new StartupHint(settings);
        _current = hint;

        hint.ClientSize = MeasureSize(settings);
        hint.Location = topLeft;
        hint.Show();
        hint.Render(settings);
        hint._dismiss.Start();
    }

    /// <summary>
    /// 在任务栏图标（系统托盘）上方显示启动提醒：锚定主屏右下角、任务栏顶边之上。
    /// </summary>
    public static void ShowNearTray(AppSettings settings)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.PrimaryScreen!.WorkingArea;
        var size = MeasureSize(settings);

        // 托盘图标在任务栏最右侧，卡片紧贴任务栏顶边、靠右 8px
        var pos = new Point(area.Right - size.Width - 8, area.Bottom - size.Height - 8);
        Show(settings, pos);
    }

    /// <summary>立即关闭（截图抓屏前必须调用，避免被拍进画面）。</summary>
    public static void CloseHint()
    {
        if (_current is { IsDisposed: false } h && h.Visible)
        {
            try { h.Close(); } catch { /* 忽略 */ }
        }
        _current = null;
    }

    /// <summary>根据内容量出面板尺寸。</summary>
    private static Size MeasureSize(AppSettings s)
    {
        // 行：图标间距 + 快捷键文本；用三个热键里最宽的估算
        var rows = new[] { s.CaptureHotkey, s.FullscreenHotkey, s.RecordHotkey };
        int maxKey = 0;
        foreach (var r in rows)
            maxKey = Math.Max(maxKey, TextRenderer.MeasureText(r, Theme.UiFontBold).Width);

        var title = TextRenderer.MeasureText("iTu 快捷键", Theme.UiFontBold);
        var foot = TextRenderer.MeasureText("点击托盘图标可进入设置", Theme.SmallFont);

        int contentW = Math.Max(maxKey + 132, Math.Max(title.Width, foot.Width));
        int width = contentW + PadX * 2 + ShadowPad * 2;

        // 标题(1) + 空行(0.6) + 三行快捷键(3) + 空行(0.6) + 脚注(1)
        int rowH = TextRenderer.MeasureText("测", Theme.UiFont).Height;
        int height = (int)(title.Height + rowH * 4.2f + foot.Height) + PadY * 2 + ShadowPad * 2;

        return new Size(Math.Max(300, width), Math.Max(170, height));
    }

    private void Render(AppSettings s)
    {
        if (!IsHandleCreated || Width <= 0) return;

        var size = ClientSize;
        using var bmp = LayeredSurface.Supersample(size, g =>
        {
            var card = new RectangleF(ShadowPad, ShadowPad,
                size.Width - ShadowPad * 2, size.Height - ShadowPad * 2);

            Theme.DrawSoftShadow(g, card, Radius, 8, 24);
            Theme.FillRounded(g, card, Radius, Theme.Surface, Theme.Border);

            float x = card.X + PadX;
            float y = card.Y + PadY;

            // 标题
            using (var brush = new SolidBrush(Theme.Text))
            {
                g.DrawString("iTu 快捷键", Theme.UiFontBold, brush, x, y);
            }
            y += TextRenderer.MeasureText("测", Theme.UiFontBold).Height + 10;

            // 三行快捷键
            DrawRow(g, ref y, s.CaptureHotkey, "自由截图");
            DrawRow(g, ref y, s.FullscreenHotkey, "全屏截图");
            DrawRow(g, ref y, s.RecordHotkey, "屏幕录制");

            y += 10;

            // 脚注
            using (var brush = new SolidBrush(Theme.TextMuted))
            {
                g.DrawString("点击托盘图标可进入设置", Theme.SmallFont, brush, x, y);
            }
        });

        LayeredSurface.Submit(Handle, bmp, Location);
    }

    /// <summary>画一行「快捷键胶囊 + 功能名」。</summary>
    private static void DrawRow(Graphics g, ref float y, string hotkey, string label)
    {
        var keySize = TextRenderer.MeasureText(hotkey, Theme.UiFontBold);

        // 快捷键胶囊
        var keyRect = new RectangleF(ShadowPad + PadX, y, keySize.Width + 16, keySize.Height + 6);
        Theme.FillRounded(g, keyRect, 6, Theme.AccentSoft, Theme.Border);

        using var keyBrush = new SolidBrush(Theme.Accent);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(hotkey, Theme.UiFontBold, keyBrush, keyRect, fmt);

        // 功能名
        using var labelBrush = new SolidBrush(Theme.Text);
        g.DrawString(label, Theme.UiFont, labelBrush,
            keyRect.Right + 12, y + (keyRect.Height - Theme.UiFont.Height) / 2f);

        y += keyRect.Height + 8;
    }
}
