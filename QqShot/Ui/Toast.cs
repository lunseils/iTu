using System.Drawing.Drawing2D;

namespace QqShot.Ui;

/// <summary>轻量浮层提示：2 秒后自动淡出。</summary>
public sealed class Toast : Form
{
    private readonly System.Windows.Forms.Timer _timer;

    /// <summary>
    /// 自持置顶定时器。
    ///
    /// 编辑器有 700ms 周期的"重申置顶"定时器，会把同属置顶层的提示条压到下面
    /// （截图上表现为提示条根本不显示）。旧实现靠"抢焦点"侥幸压在上面，
    /// 现在刻意不抢焦点，就必须自己周期性重申置顶（带 SWP_NOACTIVATE，不打扰输入）。
    /// </summary>
    private readonly System.Windows.Forms.Timer _topTimer;

    /// <summary>
    /// 当前是否真的有可见的提示条。
    ///
    /// 编辑器据此暂停自己的"重申置顶"，两者就不会再周期性互相翻层
    /// （旧行为：提示条约 83% 的时间在上层，其余时间闪到编辑器后面约 150ms）。
    /// 用弱引用列表而不是计数器，避免计数漏减导致编辑器永久失去置顶。
    /// </summary>
    public static bool AnyVisible
    {
        get
        {
            lock (Live)
            {
                for (int i = Live.Count - 1; i >= 0; i--)
                {
                    if (!Live[i].TryGetTarget(out var t) || t.IsDisposed || !t.Visible)
                    {
                        Live.RemoveAt(i);
                        continue;
                    }
                    return true;
                }
                return false;
            }
        }
    }

    private static readonly List<WeakReference<Toast>> Live = new();

    public Toast(string message, Color accent)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Surface;
        DoubleBuffered = true;

        var label = new Label
        {
            Text = message,
            Font = Theme.UiFont,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(18, 12)
        };
        Controls.Add(label);

        var dot = new Panel
        {
            BackColor = accent,
            Location = new Point(10, 17),
            Size = new Size(6, 6)
        };
        Controls.Add(dot);

        ClientSize = new Size(label.Width + 30, 40);

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => { _timer.Stop(); Close(); };

        _topTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _topTimer.Tick += (_, _) => RaiseWithoutActivating();
    }

    /// <summary>把提示条推到置顶层最上方，且不激活、不抢焦点。</summary>
    private void RaiseWithoutActivating()
    {
        if (IsDisposed || !IsHandleCreated || !Visible) return;
        Core.Native.SetWindowPos(Handle, Core.Native.HWND_TOPMOST, 0, 0, 0, 0,
            Core.Native.SWP_NOMOVE | Core.Native.SWP_NOSIZE | Core.Native.SWP_NOACTIVATE);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Timer 会创建隐藏窗口句柄，不显式释放就是一处泄漏
        _timer.Stop();
        _timer.Dispose();
        _topTimer.Stop();
        _topTimer.Dispose();

        lock (Live)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (!Live[i].TryGetTarget(out var t) || ReferenceEquals(t, this)) Live.RemoveAt(i);
            }
        }

        base.OnFormClosed(e);
    }

    /// <summary>
    /// 不抢焦点。
    ///
    /// 旧实现用 Show(owner) 会激活窗体并夺走焦点两秒：
    /// "复制"后紧接着按 Ctrl+S/Ctrl+C 会落到 Toast 上而失效，
    /// 打断用户连续快捷键操作。提示类窗口不应参与焦点竞争。
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.SetupQuality(e.Graphics);
        Theme.PaintCard(e.Graphics, ClientSize, Theme.RadiusMedium);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.ApplyRoundedRegion(this, Theme.RadiusMedium);

        var cursor = Cursor.Position;
        var area = Screen.FromPoint(cursor).WorkingArea;
        int x = Math.Clamp(cursor.X - Width / 2, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
        int y = Math.Clamp(cursor.Y + 18, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
        Location = new Point(x, y);

        lock (Live) Live.Add(new WeakReference<Toast>(this));

        _timer.Start();
        _topTimer.Start();
        RaiseWithoutActivating();
    }

    /// <summary>
    /// 关掉所有还活着的提示条。
    ///
    /// 截图前必须调一次：提示条是 TopMost 的独立窗口，留在屏幕上就会被
    /// 拍进刚截的图里（用户看到的现象就是"截图右上角多出一个窗口"）。
    /// </summary>
    public static void CloseAll()
    {
        var alive = new List<Toast>();
        lock (Live)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (!Live[i].TryGetTarget(out var t) || t.IsDisposed)
                {
                    Live.RemoveAt(i);
                    continue;
                }
                alive.Add(t);
            }
        }

        foreach (var t in alive)
        {
            try { t.Close(); } catch { /* 已销毁的忽略 */ }
        }
    }

    public static void Show(IWin32Window? owner, string message, Color accent)
    {
        // 记录弹出前的焦点窗口：ShowWithoutActivation + WS_EX_NOACTIVATE 仍不足以保证
        // 不抢焦点（.NET 10 实测：窗体样式里 NOACTIVATE=true，显示后它还是成了前台窗口），
        // 因此显示后主动把焦点还回去。提示条本身 TopMost + 定时自动关闭，不需要 owner。
        IntPtr previous = Core.Native.GetForegroundWindow();

        var toast = new Toast(message, accent);
        toast.Show();

        if (previous != IntPtr.Zero && Core.Native.GetForegroundWindow() != previous)
            Core.Native.SetForegroundWindow(previous);

        // 最后再做一次"置顶但不激活"：还焦点那一步会把编辑器重新提到置顶层最前，
        // 提示条必须重新压回最上方，否则整个提示条会被编辑器挡住看不见。
        toast.RaiseWithoutActivating();
    }
}
