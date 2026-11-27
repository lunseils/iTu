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

    // 布局间距：测量与绘制必须共用同一组数值，否则文字会溢出卡片。
    private const float TitleGap = 10f;
    private const float RowGap = 8f;
    private const float FootGap = 12f;
    private const float KeyPadX = 16f;
    private const float KeyPadY = 6f;
    private const float LabelGap = 12f;

    private const string TitleText = "iTu 快捷键";
    private const string FootText = "点击托盘图标可进入设置";

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
            cp.ExStyle |= Native.WS_EX_LAYERED;   // 逐像素 alpha（圆角 + 柔影）
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
        // 原写法 `PrimaryScreen?.WorkingArea ?? PrimaryScreen!.WorkingArea` 两侧都依赖
        // PrimaryScreen，无主屏时仍会抛 NullReferenceException；给一个真实兜底矩形。
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1032);
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

    /// <summary>根据内容量出面板尺寸（与实际绘制共用同一套布局）。</summary>
    private static Size MeasureSize(AppSettings s) => ComputeLayout(s).Canvas;

    /// <summary>
    /// 渲染到独立位图。供分层窗口提交和预览工具共用，保证"看到的就是运行的"。
    /// </summary>
    public static Bitmap RenderToBitmap(AppSettings s)
    {
        var size = MeasureSize(s);
        return LayeredSurface.Supersample(size, g => DrawContent(g, size, s));
    }

    private void Render(AppSettings s)
    {
        if (!IsHandleCreated || Width <= 0) return;
        var size = ClientSize;
        using var bmp = LayeredSurface.Supersample(size, g => DrawContent(g, size, s));
        LayeredSurface.Submit(Handle, bmp, Location);
    }

    /// <summary>统一的布局：所有 y 坐标在测量阶段一次性算好，绘制时直接复用。</summary>
    private sealed record HintLayout(
        Size Canvas,
        float X,
        float TitleY,
        float[] RowY,
        float FootY,
        float KeyW,
        float KeyH,
        float LabelX,
        float LabelH,
        string[] Hotkeys,
        string[] Labels);

    private static HintLayout ComputeLayout(AppSettings s)
    {
        // 用 GDI+ 度量（与 DrawString 一致）。旧版用 TextRenderer(GDI) 估算，
        // 行高比 DrawString 小，脚注被挤到卡片外 —— 这是"文字标签在页外"的根因。
        using var scratch = new Bitmap(1, 1);
        using var g = Graphics.FromImage(scratch);

        string[] hotkeys = { s.CaptureHotkey, s.FullscreenHotkey, s.RecordHotkey };
        string[] labels = { "自由截图", "全屏截图", "屏幕录制" };

        var titleSz = g.MeasureString(TitleText, Theme.UiFontBold);
        var footSz = g.MeasureString(FootText, Theme.SmallFont);

        float maxKeyW = 0, maxKeyH = 0;
        foreach (var hk in hotkeys)
        {
            var sz = g.MeasureString(hk, Theme.UiFontBold);
            maxKeyW = Math.Max(maxKeyW, sz.Width);
            maxKeyH = Math.Max(maxKeyH, sz.Height);
        }
        float keyW = maxKeyW + KeyPadX;
        float keyH = maxKeyH + KeyPadY;

        float labelH = 0, maxLabelW = 0;
        foreach (var lb in labels)
        {
            var sz = g.MeasureString(lb, Theme.UiFont);
            labelH = Math.Max(labelH, sz.Height);
            maxLabelW = Math.Max(maxLabelW, sz.Width);
        }

        float x = ShadowPad + PadX;
        float titleY = ShadowPad + PadY;
        float y = titleY + titleSz.Height + TitleGap;

        var rowY = new float[hotkeys.Length];
        for (int i = 0; i < rowY.Length; i++)
        {
            rowY[i] = y;
            y += keyH + RowGap;
        }
        float footY = y + (FootGap - RowGap);
        float bottom = footY + footSz.Height;

        float right = Math.Max(x + titleSz.Width,
                        Math.Max(x + footSz.Width,
                                 x + keyW + LabelGap + maxLabelW));

        int width = (int)Math.Ceiling(right + PadX + ShadowPad);
        int height = (int)Math.Ceiling(bottom + PadY + ShadowPad);

        return new HintLayout(
            new Size(Math.Max(300, width), Math.Max(160, height)),
            x, titleY, rowY, footY, keyW, keyH,
            x + keyW + LabelGap, labelH, hotkeys, labels);
    }

    private static void DrawContent(Graphics g, Size size, AppSettings s)
    {
        var card = new RectangleF(ShadowPad, ShadowPad,
            size.Width - ShadowPad * 2, size.Height - ShadowPad * 2);

        Theme.DrawSoftShadow(g, card, Radius, 8, 24);
        Theme.FillRounded(g, card, Radius, Theme.Surface, Theme.Border);

        var L = ComputeLayout(s);
        using (var brush = new SolidBrush(Theme.Text))
            g.DrawString(TitleText, Theme.UiFontBold, brush, L.X, L.TitleY);

        for (int i = 0; i < L.RowY.Length; i++)
            DrawRow(g, L, i);

        using (var brush = new SolidBrush(Theme.TextMuted))
            g.DrawString(FootText, Theme.SmallFont, brush, L.X, L.FootY);
    }

    /// <summary>画一行「快捷键胶囊 + 功能名」。</summary>
    private static void DrawRow(Graphics g, HintLayout L, int i)
    {
        var keyRect = new RectangleF(L.X, L.RowY[i], L.KeyW, L.KeyH);
        Theme.FillRounded(g, keyRect, 6, Theme.AccentSoft, Theme.Border);

        using var keyBrush = new SolidBrush(Theme.Accent);
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(L.Hotkeys[i], Theme.UiFontBold, keyBrush, keyRect, fmt);

        using var labelBrush = new SolidBrush(Theme.Text);
        g.DrawString(L.Labels[i], Theme.UiFont, labelBrush,
            L.LabelX, L.RowY[i] + (L.KeyH - L.LabelH) / 2f);
    }
}
