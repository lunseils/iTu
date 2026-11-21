using System.Drawing.Drawing2D;
using QqShot.Core;
using QqShot.Ui;

namespace QqShot;

/// <summary>
/// 录制控制面板：状态、时长、暂停/继续、停止并保存、取消。
///
/// 为什么自绘而不是摆 Label/Button：系统 Button 是直角灰底，和胶囊/圆角卡片
/// 不是一套语言；旧版面板还用一个 10×10 的方形 Panel 当"录制中"红点，
/// 出现在圆角卡片里就是一个突兀的红方块。
///
/// 关键行为：
///   · 面板优先放在**选区外**（下方→上方→右侧→左侧），避免自己被录进画面；
///   · 停止/取消都在后台线程做（编码可能几秒），面板先切到"正在保存…"，
///     期间不允许关闭，完成后才关窗，避免把 recorder 提前 Dispose 掉；
///   · 录制热键/托盘再次触发 = 停止并保存（由 <see cref="StopAndSave"/> 承接）。
/// </summary>
public sealed class RecordPanel : Form
{
    private const int CardWidth = 300;
    private const int CardHeight = 118;

    private static readonly Rectangle PauseBounds = new(16, 72, 76, 30);
    private static readonly Rectangle StopBounds = new(100, 72, 112, 30);
    private static readonly Rectangle CancelBounds = new(220, 72, 56, 30);

    private enum Phase { Active, Saving, Canceling }

    private readonly ScreenRecorder _recorder;
    private readonly Rectangle _region;

    private readonly RoundedButton _pauseBtn;
    private readonly RoundedButton _stopBtn;
    private readonly RoundedButton _cancelBtn;
    private readonly System.Windows.Forms.Timer _ui;
    private readonly Font _timerFont = new("Consolas", 15f, FontStyle.Bold, GraphicsUnit.Point);

    private readonly object _phaseGate = new();
    private Phase _phase = Phase.Active;
    private bool _allowClose;

    private TimeSpan _frozenElapsed;
    private int _frozenFrames;

    private bool _dragging;
    private Point _dragOrigin;
    private Point _windowOrigin;

    public RecordPanel(ScreenRecorder recorder)
    {
        _recorder = recorder;
        _region = recorder.Region;

        Text = "iTu 录制";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Surface;
        ClientSize = new Size(CardWidth, CardHeight);
        DoubleBuffered = true;
        KeyPreview = true;

        _pauseBtn = new RoundedButton
        {
            Text = "暂停",
            Style = RoundedButton.Kind.Ghost,
            Bounds = PauseBounds
        };
        _pauseBtn.Click += (_, _) => TogglePause();

        _stopBtn = new RoundedButton
        {
            Text = "停止并保存",
            Style = RoundedButton.Kind.Primary,
            Bounds = StopBounds
        };
        _stopBtn.Click += (_, _) => StopAndSave();

        _cancelBtn = new RoundedButton
        {
            Text = "取消",
            Style = RoundedButton.Kind.Ghost,
            Bounds = CancelBounds
        };
        _cancelBtn.Click += (_, _) => CancelRecording();

        Controls.Add(_pauseBtn);
        Controls.Add(_stopBtn);
        Controls.Add(_cancelBtn);

        _ui = new System.Windows.Forms.Timer { Interval = 250 };
        _ui.Tick += (_, _) => { SyncButtons(); Invalidate(); };
        _ui.Start();

        SyncButtons();
    }

    // ---- 对外操作 ----

    /// <summary>停止并保存（录制热键、托盘「停止录制」、Esc、Alt+F4 都走这里）。</summary>
    public void StopAndSave()
    {
        lock (_phaseGate)
        {
            if (_phase != Phase.Active) return;
            _phase = Phase.Saving;
        }

        Freeze();
        SyncButtons();
        Invalidate();

        var rec = _recorder;
        Task.Run(() =>
        {
            string? path = null;
            string? error = null;
            try { path = rec.Stop(); }
            catch (Exception ex)
            {
                error = ex.Message;
                Core.Log.Info("[record] 停止失败: " + ex);
            }

            Post(() =>
            {
                _allowClose = true;
                string msg = error != null ? "录制保存失败：" + error
                           : path == null ? "没有采集到画面，未生成文件"
                           : "录制已保存：" + Path.GetFileName(path);
                var accent = error != null ? Theme.Danger
                           : path == null ? Theme.Warning
                           : Theme.Success;

                Close();
                try { Toast.Show(null, msg, accent); } catch { }
            });
        });
    }

    private void CancelRecording()
    {
        lock (_phaseGate)
        {
            if (_phase != Phase.Active) return;
            _phase = Phase.Canceling;
        }

        Freeze();
        SyncButtons();
        Invalidate();

        var rec = _recorder;
        Task.Run(() =>
        {
            try { rec.Cancel(); }
            catch (Exception ex) { Core.Log.Info("[record] 取消失败: " + ex); }

            Post(() =>
            {
                _allowClose = true;
                Close();
                try { Toast.Show(null, "已取消本次录制", Theme.TextSecondary); } catch { }
            });
        });
    }

    private void TogglePause()
    {
        if (_phase != Phase.Active) return;

        if (_recorder.State == RecordState.Paused) _recorder.Resume();
        else _recorder.Pause();

        SyncButtons();
        Invalidate();
    }

    private void Freeze()
    {
        _frozenElapsed = _recorder.Elapsed;
        _frozenFrames = _recorder.FrameCount;
    }

    private void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch { }
    }

    private void SyncButtons()
    {
        bool active = _phase == Phase.Active;
        bool paused = _recorder.State == RecordState.Paused;

        _pauseBtn.Enabled = active;
        _pauseBtn.Text = paused ? "继续" : "暂停";

        _stopBtn.Enabled = active;
        _stopBtn.Text = _phase switch
        {
            Phase.Saving => "正在保存…",
            Phase.Canceling => "正在取消…",
            _ => "停止并保存"
        };

        _cancelBtn.Enabled = active;
    }

    // ---- 位置：优先避开录制区域 ----

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.ApplyRoundedRegion(this, Theme.RadiusMedium);
        PlaceOutsideRegion();

        Core.Log.Info($"[record] 面板 rect={Left},{Top},{Width},{Height} " +
                      $"区域={_region.X},{_region.Y},{_region.Width},{_region.Height} " +
                      $"编码={_recorder.EncoderName}");
        Core.Log.Info($"[record] 按钮 暂停={Fmt(ScreenRect(_pauseBtn))} " +
                      $"停止={Fmt(ScreenRect(_stopBtn))} 取消={Fmt(ScreenRect(_cancelBtn))}");
    }

    private Rectangle ScreenRect(Control c) => new(Left + c.Left, Top + c.Top, c.Width, c.Height);

    private static string Fmt(Rectangle r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

    private void PlaceOutsideRegion()
    {
        var work = Screen.FromRectangle(_region).WorkingArea;
        const int gap = 10;
        var size = Size;

        var candidates = new[]
        {
            new Point(_region.Left, _region.Bottom + gap),              // 下方（首选）
            new Point(_region.Left, _region.Top - size.Height - gap),   // 上方
            new Point(_region.Right + gap, _region.Top),                // 右侧
            new Point(_region.Left - size.Width - gap, _region.Top),    // 左侧
        };

        foreach (var p in candidates)
        {
            var r = new Rectangle(p, size);
            if (!work.Contains(r)) continue;
            if (r.IntersectsWith(_region)) continue;
            Location = p;
            return;
        }

        // 四个方向都放不下（例如全屏录制）：夹进工作区，允许压在画面里，用户可拖走
        var fallback = new Point(
            Math.Clamp(candidates[0].X, work.Left + 4, Math.Max(work.Left + 4, work.Right - size.Width - 4)),
            Math.Clamp(candidates[0].Y, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - size.Height - 4)));
        Location = fallback;

        if (new Rectangle(fallback, size).IntersectsWith(_region))
            Core.Log.Info("[record] 面板无法避开录制区域，会被录入画面（可拖动移出）");
    }

    // ---- 拖动 ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
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

    // ---- 键盘 ----

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            StopAndSave();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 保存/取消流程完成前不许关（否则 recorder 会被提前 Dispose）
        if (!_allowClose)
        {
            e.Cancel = true;
            StopAndSave();
            return;
        }
        base.OnFormClosing(e);
    }

    // ---- 绘制 ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.SetupQuality(g);
        Theme.PaintCard(g, ClientSize, Theme.RadiusMedium);

        bool paused = _recorder.State == RecordState.Paused;
        bool saving = _phase != Phase.Active;

        // 状态点：录制中闪烁，暂停/保存时稳定
        var accent = saving ? Theme.Accent : paused ? Theme.Warning : Theme.Danger;
        int alpha = 255;
        if (!saving && !paused)
            alpha = (Environment.TickCount64 / 500 % 2 == 0) ? 255 : 90;

        using (var dotBrush = new SolidBrush(Color.FromArgb(alpha, accent)))
            g.FillEllipse(dotBrush, 18f, 21f, 10f, 10f);

        string status = _phase switch
        {
            Phase.Saving => "正在保存…",
            Phase.Canceling => "正在取消…",
            _ => paused ? "已暂停" : "录制中"
        };

        using (var sb = new SolidBrush(Theme.Text))
            g.DrawString(status, Theme.UiFontBold, sb, 36f, 16f);

        var elapsed = saving ? _frozenElapsed : _recorder.Elapsed;
        var frames = saving ? _frozenFrames : _recorder.FrameCount;
        var clock = Format(elapsed);

        using (var tb = new SolidBrush(saving ? Theme.TextSecondary : Theme.Text))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(clock, _timerFont, tb, new RectangleF(0, 8, CardWidth - 18, 30), fmt);

        string meta = $"{_region.Width}×{_region.Height} · {_recorder.Fps}fps · " +
                      $"{frames} 帧 · {_recorder.EncoderName}";
        using (var mb = new SolidBrush(Theme.TextMuted))
            g.DrawString(meta, Theme.SmallFont, mb, 18f, 44f);

        // 分隔线：把"状态区"和"按钮区"分开，视觉上更整齐
        using (var pen = new Pen(Theme.Border))
            g.DrawLine(pen, 16f, 64.5f, CardWidth - 16f, 64.5f);
    }

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ui.Stop();
        _ui.Dispose();
        _timerFont.Dispose();
        base.OnFormClosed(e);
    }
}
