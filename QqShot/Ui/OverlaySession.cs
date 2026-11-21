using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QqShot.Core;

namespace QqShot.Ui;

/// <summary>
/// 选区会话：管理多显示器浮层、鼠标交互、窗口吸附与放大镜采样。
/// 所有坐标均为虚拟桌面物理像素。
///
/// 坐标一致性（关键）：
///   · 快照位图与坐标系共用同一个虚拟桌面矩形；
///   · 浮层局部坐标一律由窗口真实几何（PixelOrigin / PixelSize）换算，
///     不用 Monitor.Bounds —— 后者在 DPI 缩放下可能与实际窗口不符。
///
/// 输入（关键）：
///   "按住拖拽"由**两条独立通道**共同驱动，任一通道可用即可：
///     1) WH_MOUSE_LL 全局低级钩子 —— 实时、低延迟，但可能被系统静默摘除
///        （回调超时/安全软件拦截/提权窗口 UIPI）；
///     2) 16ms 轮询 GetCursorPos + GetAsyncKeyState —— 不依赖消息路由，
///        用于兜底：合成 Down/Move/Up，保证"按下即开始、移动即跟手、松开即结束"。
///   两条通道共用同一套状态机，重复事件由 _dragging / _current 幂等吸收。
///
/// 性能约定：
///   · 放大镜取样复用位图，且直接从快照内存取像素（不抓屏、不 GetPixel）；
///   · 重绘只失效真正变化的区域：拖拽时是"新旧选区的差集细带"，而非整个选区；
///   · 等待事件用 MsgWaitForMultipleObjects 带超时阻塞，不空转。
/// </summary>
public sealed class OverlaySession : IDisposable
{
    private const int MagnifierGrid = 11;

    /// <summary>轮询兜底通道的周期（≈60Hz）。</summary>
    private const int PollIntervalMs = 16;

    /// <summary>超过该时长没有任何鼠标事件就写一条告警，便于用户回报问题。</summary>
    private const int NoInputWarnMs = 3000;

    private readonly List<OverlayForm> _forms = new();
    private readonly List<MonitorInfo> _monitors;
    private readonly Rectangle _virtualBounds;
    private readonly AppSettings _settings;

    /// <summary>
    /// 当前截图模式。顶部功能条的下拉菜单可以在会话内切换（自由/窗口/全屏），
    /// 所以这里不是 readonly。
    /// </summary>
    private CaptureMode _mode;

    /// <summary>每个浮层复用一张放大镜取样图，避免高频分配。</summary>
    private readonly Dictionary<OverlayForm, Bitmap> _magnifierBuffers = new();

    /// <summary>快照像素取样器（一次 LockBits → 纯内存读取）。</summary>
    private SnapshotSampler? _sampler;

    private Bitmap? _snapshot;
    private List<WindowTarget> _windows = new();
    private Point _start;
    private Point _current;
    private bool _dragging;
    private bool _finished;

    // 上一帧状态，用于计算脏区
    private Rectangle _lastSelection = Rectangle.Empty;
    private Point _lastCursor = new(int.MinValue, int.MinValue);
    private Rectangle? _lastSnap;
    private Rectangle? _lastHover;
    private bool _lastMagVisible;

    /// <summary>鼠标下那个窗口（整块会被高亮，点一下就变成选区）。</summary>
    private WindowTarget? _hoverTarget;
    private Rectangle? _hoverWindow;
    private Rectangle? _snapHint;
    private string? _hoverTitle;

    public OverlaySession(AppSettings settings, CaptureMode mode = CaptureMode.Region)
    {
        _settings = settings;
        _mode = mode;
        _monitors = DisplayInfo.GetMonitors();
        _virtualBounds = DisplayInfo.GetVirtualBounds(_monitors);
    }

    /// <summary>选区结果（虚拟桌面坐标）。</summary>
    public Rectangle Selection { get; private set; } = Rectangle.Empty;

    /// <summary>选区内的图像。</summary>
    public Bitmap? Result { get; private set; }

    /// <summary>
    /// 是否有浮层会话进行中。
    ///
    /// 编辑器有 700ms 周期的"重申置顶"定时器；如果截图时编辑器还开着，
    /// 它会反复把自己顶到新浮层之上，遮住待选区域，还会被拍进新快照。
    /// 编辑器在自顶前检查本标志即可互斥（退出会话后自动恢复自顶）。
    /// </summary>
    public static bool IsSessionActive => Volatile.Read(ref _activeSessions) > 0;

    private static int _activeSessions;

    // 输入诊断计数
    private int _downCount;
    private int _moveCount;
    private int _upCount;
    private bool _noInputWarned;

    /// <summary>胶囊工具栏的屏幕矩形（画在遮罩层上，不是独立窗口）。</summary>
    private Rectangle _pillScreen;

    /// <summary>胶囊上悬停的格位（-1 无）。</summary>
    private int _pillHover = -1;

    /// <summary>用户点的是胶囊上的"保存"：出图后直接写文件，不再进编辑器。</summary>
    public bool SaveRequested { get; private set; }

    /// <summary>用户点了"钉在桌面"：出图后直接钉出。</summary>
    public bool PinRequested { get; private set; }

    /// <summary>用户点了"分享"：出图后弹分享菜单。</summary>
    public bool ShareRequested { get; private set; }

    /// <summary>翻译是否已写回选区画面（原文被背景色填掉、原位换成译文）。</summary>
    public bool TranslateApplied { get; private set; }

    /// <summary>导出时是否加柔和投影（参考图里的"阴影"勾选）。</summary>
    public bool ShadowEnabled { get; private set; }

    /// <summary>当前选中的绘制工具（null = 只调选区）。</summary>
    public Core.AnnotationKind? Tool { get; private set; }

    private readonly List<Core.Annotation> _strokes = new();

    /// <summary>被撤销掉的笔画（"重做"取回用；新画一笔即作废）。</summary>
    private readonly List<Core.Annotation> _redo = new();

    private Core.Annotation? _active;
    private bool _inkActive;

    /// <summary>当前笔画颜色。</summary>
    private Color _inkColor = Color.FromArgb(240, 118, 114);

    /// <summary>当前笔画粗细。</summary>
    private float _inkWidth = 2.4f;

    /// <summary>形状是否填充。</summary>
    private bool _inkFill;

    /// <summary>文字字号。</summary>
    private float _inkFontSize = 16f;

    /// <summary>次级胶囊（同层手绘：子类型/粗细/填充/字号/色块）。</summary>
    private ToolFlyout? _fly;
    private bool _flyVisible;
    private PillAction _flyOwner = PillAction.None;

    /// <summary>颜色弹层（同层手绘）。</summary>
    private ColorPickerChrome? _popup;

    /// <summary>顶部居中的功能条（同层手绘，参考图二）。</summary>
    private readonly QuickBar _bar = new();

    /// <summary>正在拖动功能条（记录按下点与条左上角的偏移）。</summary>
    private bool _barDrag;
    private Point _barGrab;

    /// <summary>本次会话里被"隐藏当前窗口"最小化掉的窗口（会话结束还原）。</summary>
    private readonly List<IntPtr> _hiddenWindows = new();

    /// <summary>用户在顶部功能条上点了「录屏」：会话结束后直接进入录制流程。</summary>
    public bool RecordRequested { get; private set; }

    /// <summary>遮罩层上画好的标注（屏幕坐标）。</summary>
    public IReadOnlyList<Core.Annotation> Strokes => _strokes;

    /// <summary>圆角胶囊（贴在同一行、胶囊右侧）的屏幕矩形。</summary>
    public Rectangle RadiusBarScreen { get; private set; } = Rectangle.Empty;

    private int _lastRadius = -1;
    private int _lastStrokeCount = -1;
    private bool _lastDragging;
    private Rectangle _lastPillScreen;
    private Rectangle _lastRadiusBarScreen;
    private bool _lastShadowEnabled;
    private int _lastPillHover = -1;

    /// <summary>选区调节的抓取点。</summary>
    public enum Grip { None, New, Move, L, R, T, B, TL, TR, BL, BR }

    private Grip _grip = Grip.None;

    /// <summary>正在拖圆角滑杆（按住时跟随鼠标）。</summary>
    private bool _radiusDrag;
    private Rectangle _radiusSlider;
    private Rectangle _gripRect;          // 拖动开始时的选区
    private Point _gripStart;

    /// <summary>圆角半径（图像像素），保存时生效，拖动中实时预览。</summary>
    public int Radius { get; private set; }

    /// <summary>开始选区。返回 true 表示用户确认了选区。</summary>
    public bool Run(CaptureMode mode)
    {
        // 实例复用时清掉上一轮的状态，避免"取消"后再次 Run 直接返回旧图
        _mode = mode;
        _finished = false;
        Result = null;
        SaveRequested = PinRequested = ShareRequested = false;
        TranslateApplied = false;
        ShadowEnabled = false;
        Radius = 0;
        Tool = null;
        Selection = Rectangle.Empty;
        _strokes.Clear();
        DropRedo();
        _active = null;
        _inkActive = false;
        _dragging = false;
        _grip = Grip.None;
        _radiusDrag = false;
        _radiusSlider = Rectangle.Empty;
        _lastSelection = Rectangle.Empty;
        _lastCursor = new Point(int.MinValue, int.MinValue);
        _lastSnap = _lastHover = null;
        _lastPillScreen = _lastRadiusBarScreen = Rectangle.Empty;
        _lastFlyRect = _lastPickerRect = Rectangle.Empty;
        _lastHint = null;
        _lastHintPos = Point.Empty;
        _chromeDirty = false;
        _flyVisible = false;
        _popup = null;
        _lastPillHover = -1;
        _lastRadius = -1;
        _lastStrokeCount = -1;
        _lastDragging = false;
        _lastShadowEnabled = false;
        _downCount = _moveCount = _upCount = 0;
        _noInputWarned = false;
        _lastDownTick = 0;
        _downConsumed = false;
        _textBusy = false;
        _statusText = null;
        _statusOwner = PillAction.None;
        _statusUntil = 0;
        _statusTimer?.Stop();
        _sessionClock.Restart();

        RecordRequested = false;
        _barDrag = false;
        _rightWasDown = false;
        _lastRightTick = 0;
        _hoverTarget = null;
        _hoverWindow = null;
        _hoverTitle = null;
        _lastHover = null;
        _hiddenWindows.Clear();
        _lastBarRect = Rectangle.Empty;

        // 快照与坐标换算必须共用同一个虚拟桌面矩形
        var desktop = ScreenCapture.CaptureDesktop(_virtualBounds);
        _snapshot = desktop.Image;
        _windows = desktop.Windows;
        _sampler = new SnapshotSampler(_snapshot);

        // 顶部居中的功能条：本次会话固定位置，用户可拖动
        _bar.Visible = true;
        _bar.Collapsed = false;
        _bar.MenuOpen = false;
        _bar.CurrentMode = _mode switch
        {
            CaptureMode.Window => QuickBar.Menu.Window,
            CaptureMode.FullScreen => QuickBar.Menu.Fullscreen,
            _ => QuickBar.Menu.Region
        };
        _bar.PlaceTopCenter(_virtualBounds);
        _bar.Changed -= OnChromeChanged;
        _bar.Changed += OnChromeChanged;

        Log.Info($"[overlay] 会话开始 mode={mode} 显示器={_monitors.Count} " +
                 $"虚拟桌面={_virtualBounds} 快照={_snapshot.Width}x{_snapshot.Height} " +
                 $"放大镜={_settings.ShowMagnifier} 可选窗口={_windows.Count} " +
                 $"功能条={_bar.Bounds} 截图按钮={_bar.CaptureButtonBounds} 圆圈={_bar.CheckBounds}");

        // 整个编辑器只有一个分层窗口，覆盖整个虚拟桌面（含负坐标显示器）。
        // 选区、圆角条、胶囊、次级胶囊、颜色弹层、标注、放大镜都画在它的同一张位图上。
        var form = new OverlayForm(_virtualBounds)
        {
            Snapshot = _snapshot,
            MagnifierVisible = _settings.ShowMagnifier
        };
        form.KeyDown += OnKeyDown;
        _forms.Add(form);
        if (_settings.ShowMagnifier)
            _magnifierBuffers[form] = new Bitmap(MagnifierGrid, MagnifierGrid, PixelFormat.Format32bppArgb);

        try
        {
            Interlocked.Increment(ref _activeSessions);

            form.Show();

            // 定位后立刻同步真实几何，后续所有换算都基于它
            Native.SetWindowPos(form.Handle, new IntPtr(-1),
                _virtualBounds.Left, _virtualBounds.Top,
                _virtualBounds.Width, _virtualBounds.Height,
                Native.SWP_SHOWWINDOW);
            form.SyncWindowMetrics();

            // 预热同一张位图（压暗底图 + 首帧提交），避免第一次鼠标事件撞上合成开销
            form.Prepare();

            foreach (var f in _forms)
                Log.Info($"[overlay] 浮层 hwnd=0x{f.Handle.ToInt64():X} " +
                         $"origin={f.PixelOrigin} size={f.PixelSize} visible={f.Visible}");

            Native.GetCursorPos(out var p);
            _current = new Point(p.X, p.Y);
            UpdateCursor(_current);

            // 唯一浮层负责键盘焦点（Esc / Enter / 文字输入需要）
            var focusForm = _forms.FirstOrDefault();
            if (focusForm != null)
            {
                focusForm.Activate();
                focusForm.Focus();
                Log.Info($"[overlay] 焦点浮层 hwnd=0x{focusForm.Handle.ToInt64():X} " +
                         $"前台={Native.GetForegroundWindow() == focusForm.Handle}");
            }

            if (mode == CaptureMode.Window)
            {
                UpdateHoverWindow();
            }
            else
            {
                UpdateMagnifier();
            }
            Redraw();

            // 用全局低级鼠标钩子 + 16ms 轮询双通道驱动选区
            StartInput();

            // 阻塞等待用户完成选区（Esc 取消 / Enter 确认 / 拖拽抬手）
            PumpUntilFinished();
        }
        finally
        {
            // 先解除"浮层进行中"，会话结束后编辑器可以恢复自顶
            Interlocked.Decrement(ref _activeSessions);

            StopInput();

            // 「隐藏当前窗口」是临时把目标窗口最小化：会话一结束就还原，
            // 否则用户会莫名其妙少一个窗口（还以为程序把它关掉了）。
            RestoreHiddenWindows();

            _bar.Visible = false;
            _bar.Changed -= OnChromeChanged;

            foreach (var f in _forms)
            {
                f.Close();
                f.Dispose();
            }
            _forms.Clear();

            foreach (var b in _magnifierBuffers.Values) b.Dispose();
            _magnifierBuffers.Clear();
        }

        Log.Info($"[overlay] 会话结束 选区={Selection.Width}x{Selection.Height} " +
                 $"down={_downCount} move={_moveCount} up={_upCount}");

        try
        {
            if (Selection.Width > 0 && Selection.Height > 0 && _snapshot != null)
            {
                var clipped = Rectangle.Intersect(Selection, _virtualBounds);

                // 完全落在虚拟桌面之外时不要兜底成 1×1 —— 那会变成一张"纯白小图"
                if (clipped.Width <= 0 || clipped.Height <= 0)
                {
                    Log.Info("[overlay] 选区与桌面无交集，放弃产出");
                    return false;
                }

                // 真正截屏发生在"保存"这一刻：浮层已关闭，给 DWM 一点时间撤掉，
                // 再从活屏抓一次；抓取失败退回会话快照。
                PumpMessages(140);

                Result = GrabLive(clipped) ?? CropSnapshot(clipped);

                // 标注合成到截图上（先偏移到图片坐标再画）
                if (_strokes.Count > 0)
                {
                    var shifted = _strokes.Select(a =>
                    {
                        var c = a.Clone();
                        ShiftBy(c, -clipped.Left, -clipped.Top);
                        return c;
                    }).ToList();
                    Ui.OverlayInk.Composite(Result, shifted);

                    // 预览与出图各自缓存了 Font / 中间位图，出图完成即可释放
                    foreach (var a in _strokes) a.ReleaseCachedResources();
                    foreach (var a in shifted) a.ReleaseCachedResources();
                }

                // 圆角：裁掉四角，导出后是透明（不是白角）
                if (Radius > 0) Result = ApplyRadius(Result, Radius);

                // 勾了"阴影"：再叠一层柔和投影（会略微扩大画布）
                if (ShadowEnabled) Result = Core.ImageRounder.AddShadow(Result, Radius);
            }

            return Result != null;
        }
        finally
        {
            // 合成/圆角/阴影任一步抛异常也必须释放快照，否则复用会话会一直占着全屏位图。
            _snapshot?.Dispose();
            _snapshot = null;
            _sampler = null;
        }
    }

    private bool ContainsCursor(OverlayForm f)
    {
        f.SyncWindowMetrics();
        var r = new Rectangle(f.PixelOrigin, f.PixelSize);
        return r.Contains(_current);
    }

    /// <summary>
    /// 阻塞式消息循环。
    ///
    /// 用 MsgWaitForMultipleObjects 带超时等待：
    ///   · 有消息到达 → 立即返回并处理，响应无延迟；
    ///   · 无消息 → 线程休眠至多 50ms，不占 CPU；
    ///   · 每轮重新检查浮层是否仍存在，避免被外部关闭后无限挂死。
    /// </summary>
    private void PumpUntilFinished()
    {
        while (!_finished)
        {
            Application.DoEvents();

            if (_finished) break;

            // 浮层被外部关闭时结束会话，避免永久挂死
            if (_forms.Count == 0 || _forms.TrueForAll(f => f.IsDisposed || !f.Visible))
            {
                _finished = true;
                break;
            }

            Native.MsgWaitForMultipleObjects(0, IntPtr.Zero, false, 50, Native.QS_ALLINPUT);
        }
    }

    // ---- 输入：低级钩子（主）+ 16ms 轮询（兜底），共用同一状态机 ----

    private MouseHook? _hook;
    private System.Windows.Forms.Timer? _poll;

    private void StartInput()
    {
        _hook = new MouseHook();
        _hook.Down += e =>
        {
            if (_downCount++ == 0) LogFirstEvent("down", e);
            OnPointerDown(e.Position);
        };
        _hook.Move += e =>
        {
            if (_moveCount++ == 0) LogFirstEvent("move", e);
            OnPointerMove(e.Position);
        };
        _hook.Up += e =>
        {
            if (_upCount++ == 0) LogFirstEvent("up", e);
            OnPointerUp(e.Position);
        };
        // 右键：截图界面里随处可退出（菜单/弹层开着时先收起它们）
        _hook.RightDown += e => OnRightClick(e.Position);
        _hook.Install();

        // 轮询兜底：钩子被系统摘除 / 事件被丢弃 / 合成输入不走钩子时，
        // 仍能保证"按下开始、按住移动跟手、松开结束"。
        _poll = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _poll.Tick += (_, _) => PollInput();
        _poll.Start();

        Log.Info($"[input] 输入通道就绪 hook={_hook.IsInstalled} poll={PollIntervalMs}ms");
    }

    private void LogFirstEvent(string kind, MouseHookEvent e) =>
        Log.Info($"[input] 首次 {kind} 事件 坐标=({e.Position.X},{e.Position.Y}) " +
                 $"注入={e.Injected}");

    private void StopInput()
    {
        _poll?.Stop();
        _poll?.Dispose();
        _poll = null;

        _hook?.Dispose();
        _hook = null;
    }

    /// <summary>轮询兜底通道：不依赖消息路由，直接读物理光标与按键状态。</summary>
    private void PollInput()
    {
        if (_finished || _modalOpen || _forms.Count == 0) return;

        // 功能条的状态提示是按时间到点消失的，借这个 16ms 心跳来收尾
        _bar.ClearStatusIfExpired();

        Native.GetCursorPos(out var p);
        var pt = new Point(p.X, p.Y);
        bool down = Native.IsLeftButtonDown();

        // 右键兜底：钩子被摘除 / 事件被丢弃时，轮询仍能感知"右键按下"。
        // 与钩子通道共用 260ms 去重，不会重复触发。
        bool rightDown = Native.IsRightButtonDown();
        if (rightDown && !_rightWasDown) OnRightClick(pt);
        _rightWasDown = rightDown;

        if (down)
        {
            if (_inkActive)
            {
                // 正在画标注：左键持续按下期间一律续画（轮询兜底）
                if (pt != _current) OnPointerMove(pt);
            }
            else if (!_dragging && !_downConsumed)
            {
                OnPointerDown(pt);       // 兜底：钩子没送出 Down 也能开始拖拽
            }
            else if (pt != _current)
            {
                // 按下已被钩子消费（例如点击胶囊）：轮询只负责悬停反馈，
                // 不能再次投递 Down，否则按住按钮会每 120ms 重复触发一次动作。
                OnPointerMove(pt);
            }
        }
        else if (_inkActive)
        {
            OnPointerUp(pt);             // 松开：结束这一笔
        }
        else if (_dragging)
        {
            OnPointerUp(pt);             // 兜底：钩子没送出 Up 也能正常收尾
        }
        else if (pt != _current)
        {
            // 悬停兜底：钩子被系统摘除、或输入来自 SetCursorPos/远程桌面这类
            // 不产生 WM_MOUSEMOVE 的移动时，窗口高亮与光标形态全靠这条通道维持。
            // 这里只转发 Move，绝不投递 Down，所以不会重复触发点击。
            OnPointerMove(pt);
        }

        if (!_noInputWarned && _downCount == 0 && _moveCount == 0 &&
            _sessionClock.ElapsedMilliseconds > NoInputWarnMs)
        {
            _noInputWarned = true;
            Log.Info("[input] 警告：3 秒内没有收到任何鼠标事件。若此时拖动无效，" +
                     "说明浮层未获得输入（检查是否有旧版本实例占用、或浮层是否真的显示）。");
        }
    }

    private readonly System.Diagnostics.Stopwatch _sessionClock = System.Diagnostics.Stopwatch.StartNew();

    private long _lastDownTick;

    /// <summary>本帧右键是否按住（轮询兜底用：钩子被摘掉时仍能右键退出）。</summary>
    private bool _rightWasDown;

    private long _lastRightTick;

    /// <summary>
    /// 右键 = 随处退出截图（需求 3）。
    ///
    /// 不做"看场景分行为"的处理：无论光标在选区内、控件上、功能条上，
    /// 右键都是退出 —— 用户要的就是一个随时能走的出口。
    /// 唯一的例外是系统取色框打开期间（那是另一个模态窗口自己的右键）。
    /// </summary>
    private void OnRightClick(Point p)
    {
        if (_finished || _modalOpen) return;

        long now = Environment.TickCount64;
        if (now - _lastRightTick < 260) return;   // 钩子 + 轮询去重
        _lastRightTick = now;

        if (_popup != null) DismissPopup();
        if (_flyVisible) HideFlyout();

        Log.Info($"[input] 右键退出截图 at={p.X},{p.Y}");
        Selection = Rectangle.Empty;
        Finish();
    }


    /// <summary>本次物理按下是否已被状态机消费；防止轮询通道在按住期间重复触发同一点击。</summary>
    private bool _downConsumed;

    /// <summary>
    /// 模态取色框打开期间为 true。输入走的是全局低级鼠标钩子，它不认识窗口归属，
    /// 取色框上的点击会原样送进状态机（选区被清空重画、点空白处还会结束会话）。
    /// 所以模态期间三个入口一起早退。
    /// </summary>
    private bool _modalOpen;

    private void OnPointerDown(Point p)
    {
        if (_finished || _dragging || _inkActive || _modalOpen) return;

        // 钩子与轮询兜底可能把同一次按下投递两遍（工具会被切成"开→关"），
        // 120ms 内的连续下行按一次处理。
        long now = Environment.TickCount64;
        if (now - _lastDownTick < 120) return;
        _lastDownTick = now;
        _downConsumed = true;

        _radiusDrag = false;

        // 顶部功能条：它画在最上层，命中也要排在最前。
        // 菜单先判：点菜单项 = 切换截图方式；点菜单外面 = 只收起菜单（不开始框选）。
        if (_bar.Visible && _bar.MenuOpen)
        {
            int mi = _bar.MenuItemAt(p);
            if (mi >= 0)
            {
                CloseBarMenu();
                OnBarMenu((QuickBar.Menu)mi);
                return;
            }
            if (!_bar.Contains(p) && !_bar.ChipContains(p))
            {
                CloseBarMenu();
                return;
            }
        }

        // 收起态：小柄点一下把整条展开
        if (_bar.Visible && _bar.Collapsed)
        {
            if (_bar.ChipContains(p))
            {
                _bar.Collapsed = false;
                Log.Info("[bar] 展开");
                Redraw();
            }
            return;
        }

        // 按在条上的非按钮区域 = 拖动整条（参考图二的六点手柄就是这个用途）。
        if (_bar.Visible && _bar.Contains(p))
        {
            int hit = _bar.HitTest(p);
            if (hit == -1)
            {
                _barDrag = true;
                _barGrab = new Point(p.X - _bar.Bounds.Left, p.Y - _bar.Bounds.Top);
                Log.Info($"[bar] 开始拖动 位置={_bar.Bounds} 抓取点={_barGrab}");
            }
            else   // 外层已保证 Visible && !Collapsed && 点在条内，HitTest 只会返回 -1 或 >=0
            {
                OnBarButton((QuickBar.Btn)hit);
            }
            return;
        }

        // 命中优先级：颜色弹层 > 次级胶囊 > 主胶囊。
        // 三者都画在同一个分层窗口上，全部由这里统一分发。
        if (_popup != null)
        {
            if (_popup.Contains(p))
            {
                _popup.PointerDown(p);
                Redraw();
                return;
            }

            // 点了弹层以外的任何地方（另一个工具、主胶囊、选区、空白处）都要先收起色板。
            // 以前只在"选中颜色"或"收起次级胶囊"时才关，于是"弹着色板去选别的工具"
            // 会把色板一直留在画面上，看着像卡住了。
            bool onSwatch = _flyVisible && _fly is { } f && f.IsSwatchAt(p);
            DismissPopup();
            Log.Info($"[picker] dismiss reason=外部点击 at={p.X},{p.Y}");

            // 再点一次色块 = 收起（不能"关了又开"，观感是点了没反应）
            if (onSwatch)
            {
                Redraw();
                return;
            }
        }
        if (_flyVisible && _fly is { } fly && fly.Contains(p))
        {
            fly.PointerDown(p);
            Redraw();
            return;
        }

        // 胶囊命中（同一个图层，直接坐标命中）：点保存/取消/工具都在这里分发
        if (_pillScreen.Width > 8 && _pillScreen.Contains(p))
        {
            int cell = Ui.PillChrome.HitTest(_pillScreen, new Point(p.X - _pillScreen.Left, p.Y - _pillScreen.Top));
            if (cell >= 0)
            {
                OnPillActionClicked(Ui.PillChrome.ActionAt(cell));
                return;
            }
            return;   // 胶囊空白处：吞掉
        }

        // 文字工具：点击弹输入框，输入完成后创建文字标注
        if (Tool == Core.AnnotationKind.Text && Selection.Contains(p))
        {
            BeginTextInput(p);
            return;
        }

        // 已选绘制工具且点在选区内：直接在遮罩层上画，不碰选区
        if (Tool is { } tool && Selection.Contains(p))
        {
            _inkStart = new PointF(p.X, p.Y);
            _active = NewStroke(tool, _inkStart);
            _inkActive = _active != null;
            if (_active != null)
            {
                DropRedo();          // 新笔画让"重做"的历史作废（与常规编辑器语义一致）
                _strokes.Add(_active);
                Redraw();
                return;
            }
        }

        // 已有选区时：圆角工具条 → 控制点，都要在"清空控件"之前判定
        if (Selection.Width > 8 && Selection.Height > 8)
        {
            if (HandleToolbarClick(p)) return;

            // 边框外的"扩拉线"已取消（需求：中间控制点旁边的线不要了），
            // 拉边直接拖边中点的圆圈。
            _grip = GripAt(Selection, p);   // 8 个把手：四角 + 四边中点
            if (_grip != Grip.None)
            {
                _gripRect = Selection;
                _gripStart = p;
                _dragging = true;
                return;
            }
        }

        HideChrome();     // 真的要重画选区了，收掉胶囊与圆角条

        _start = _current = p;

        if (_mode == CaptureMode.Window)
        {
            UpdateHoverWindow();
            if (_hoverTarget is { } hit)
            {
                Selection = Rectangle.Intersect(hit.Bounds, _virtualBounds);
                Finish();
            }
            return;
        }

        _dragging = true;
        Selection = Rectangle.Empty;

        // 开始新框选：把"悬停整窗高亮"收掉，否则整窗框会和正在拉大的选区框叠在一起
        _hoverTarget = null;
        _hoverWindow = null;
        _hoverTitle = null;

        UpdateMagnifier();
        UpdateCursor(p);
        Redraw();
    }

    private void OnPointerMove(Point p)
    {
        if (_finished || _modalOpen) return;
        if (p == _current) return;
        _current = p;

        // 拖动功能条：整条跟着鼠标走（不碰选区、不碰任何标注）
        if (_barDrag)
        {
            _bar.MoveTo(new Point(p.X - _barGrab.X, p.Y - _barGrab.Y), _virtualBounds);
            UpdateCursor(p);
            Redraw();
            return;
        }

        // 光标停在功能条 / 下拉菜单 / 收起小柄上：只更新条自身的悬停态
        bool onBar = _bar.Visible &&
                     (_bar.Contains(p) || _bar.MenuItemAt(p) >= 0 || _bar.ChipContains(p));
        if (onBar)
        {
            _bar.PointerMove(p);
            _barDrag = false;
            HideMagnifier();
            UpdateCursor(p);
            Redraw();
            return;
        }
        _bar.PointerLeave();

        // 弹层/次级胶囊有自己的悬停与滑块拖动；它们消费事件时不改选区
        if (_popup != null)
        {
            _popup.PointerMove(p);
            if (_popup.Contains(p)) { HideMagnifier(); Redraw(); return; }
            _popup.PointerLeave();
        }
        // 拖调节条时指针几乎必然滑出飞层（它只有一行高）：只要还在拖，
        // 就把移动继续喂给飞层，否则值会停在半路，事件还会继续往下走变成别的操作。
        if (_flyVisible && _fly is { } fly && (fly.Contains(p) || fly.DraggingSlider))
        {
            fly.PointerMove(p);
            HideMagnifier();
            Redraw();
            return;
        }
        if (_flyVisible && _fly != null && _fly.HintText != null) { _fly.PointerLeave(); Redraw(); }

        if (_active != null)
        {
            ExtendStroke(_active, _inkStart, p);
            Redraw();
            return;
        }

        if (_radiusDrag && !_radiusSlider.IsEmpty)
        {
            SetRadiusFromX(p.X, _radiusSlider);
            return;
        }

        // 胶囊悬停高亮（同一图层的鼠标反馈）
        if (!_pillScreen.IsEmpty && !_dragging)
        {
            int cell = _pillScreen.Contains(p)
                ? Ui.PillChrome.HitTest(_pillScreen, new Point(p.X - _pillScreen.Left, p.Y - _pillScreen.Top))
                : -1;
            _pillHover = cell;
        }

        if (_dragging && _grip != Grip.None)
        {
            Selection = ResizeBy(_gripRect, _gripStart, p, _grip);

            // 拖控制点/整体平移时，圆角条与胶囊必须跟着选区走。
            // 以前只在"松手"时重排，而且 _grip != None 那条分支根本不重排 ——
            // 于是移动整块脏区时，两个控件都留在原地，像被钉在屏幕上。
            //
            // 也不再按"选区够不够大"决定显不显示：胶囊尺寸固定之后（595×38），
            // 窄选区的胶囊照样放得下，140×70 的门槛只会让用户把框缩小一点就
            // 找不到编辑工具（用户报的 1、2）。
            if (Selection.Width > 0 && Selection.Height > 0) LayoutChrome();
            else HideChrome();
        }
        else if (_dragging)
        {
            Selection = Normalize(_start, _current);
            ApplySnap();
        }
        else
        {
            UpdateHoverWindow();
        }

        UpdateMagnifier();
        UpdateCursor(p);
        Redraw();
    }

    // ---------------- 光标形态 ----------------

    private Cursor? _cursor;
    private string _cursorName = "default";

    /// <summary>
    /// 按"鼠标下是什么"决定光标：脏区里是移动、控制点上是缩放、控件上是手型、
    /// 其余是十字（截图态默认）。只在形态真的变化时下发，避免每个鼠标消息都设一次。
    /// </summary>
    private void UpdateCursor(Point p)
    {
        var (wanted, name) = ComputeCursor(p);
        if (ReferenceEquals(wanted, _cursor) && name == _cursorName) return;

        _cursor = wanted;
        _cursorName = name;
        Log.Info($"[cursor] {name} at={p.X},{p.Y}");   // 真机脚本据此断言"脏区里是移动光标"
        foreach (var f in _forms) f.DesiredCursor = wanted;
    }

    private (Cursor Cursor, string Name) ComputeCursor(Point p)
    {
        if (_bar.Visible)
        {
            if (_bar.OnDragHandle(p)) return (Cursors.SizeAll, "bar-drag");
            if (_bar.ChipContains(p)) return (Cursors.Hand, "bar-chip");
            if (_bar.MenuItemAt(p) >= 0) return (Cursors.Hand, "bar-menu");
            if (_bar.Contains(p)) return (Cursors.Hand, "bar");
        }

        if (_popup?.Contains(p) == true) return (Cursors.Hand, "picker");
        if (_flyVisible && _fly?.Contains(p) == true) return (Cursors.Hand, "flyout");
        if (_pillScreen.Width > 8 && _pillScreen.Contains(p)) return (Cursors.Hand, "pill");

        if (Selection.Width > 8 && Selection.Height > 8)
        {
            var grip = GripAt(Selection, p);
            if (grip != Grip.None)
            {
                // 脏区内部：没选工具时是"移动整块"，选了工具就是画标注的十字
                if (grip == Grip.Move) return (Tool == null ? Cursors.SizeAll : Cursors.Cross, "move");
                return (CursorFor(grip, false), grip.ToString());
            }
        }

        return (Cursors.Cross, "cross");
    }

    private static Cursor CursorFor(Grip grip, bool toolActive) => grip switch
    {
        Grip.TL or Grip.BR => Cursors.SizeNWSE,
        Grip.TR or Grip.BL => Cursors.SizeNESW,
        Grip.T or Grip.B => Cursors.SizeNS,
        Grip.L or Grip.R => Cursors.SizeWE,
        Grip.Move => toolActive ? Cursors.Cross : Cursors.SizeAll,
        _ => Cursors.Cross
    };

    /// <summary>
    /// 布置底部控件：胶囊工具栏 + 圆角条 —— 全部画在遮罩层这一个图层上。
    ///
    /// 两条硬约束：
    ///   1) 谁都不能压住选区（脏区里是用户要截的画面，也是文字识别/翻译的目标）；
    ///   2) 圆角条不能在工具胶囊上方 —— 否则点完截图工具的视觉重心变成"两条叠在一起"。
    ///
    /// 所以圆角条的落位顺序是：**选区内侧上方 → 选区左右空白区 → 胶囊下方**，
    /// 贴顶截图（上方没位置）时自然会落到左右的空白区，而不是挤到胶囊上面去。
    /// </summary>
    private void LayoutChrome()
    {
        // 只有"还没有选区"才没有工具。以前这里是 140×70 的门槛 —— 那是胶囊宽度
        // 还随选区缩放的年代留下的：选得太窄，缩小的胶囊挤不下图标。
        // 现在胶囊尺寸固定，再卡这个门槛就变成"把框缩小一点，编辑工具全消失"。
        if (Selection.Width <= 0 || Selection.Height <= 0)
        {
            _pillScreen = Rectangle.Empty;
            RadiusBarScreen = Rectangle.Empty;
            return;
        }

        const int edge = 6;

        // 胶囊尺寸固定（用户明确要求：不随截图大小变化）。
        // 以前 width = min(595, 选区宽-28) 会让窄选区的胶囊跟着缩，
        // 图标、格位中心、点击坐标全部按比例缩放 —— 看着"忽大忽小"。
        int width = Math.Min(PillToolbar.BaseSize.Width, Math.Max(240, _virtualBounds.Width - edge * 2));
        int height = PillToolbar.BaseSize.Height;

        int x = Selection.Left + (Selection.Width - width) / 2;
        x = Math.Clamp(x, _virtualBounds.Left + edge,
            Math.Max(_virtualBounds.Left + edge, _virtualBounds.Right - width - edge));

        // 胶囊：默认选区下方 16px；下方放不下 → 翻到选区上方
        int pillY = Selection.Bottom + 16;
        if (pillY + height > _virtualBounds.Bottom - edge)
        {
            pillY = Selection.Top - height - 12;
            if (pillY < _virtualBounds.Top + edge)
                pillY = Math.Max(_virtualBounds.Top + edge, _virtualBounds.Bottom - height - edge);
        }

        _pillScreen = new Rectangle(x, pillY, width, height);
        RadiusBarScreen = PlaceRadiusBar(edge);
    }

    /// <summary>圆角条尺寸：比参考图（324×24）放大一圈 —— 24px 高时 8pt 字挤成一条，看不清。</summary>
    private const int BarW = 380;
    private const int BarH = 34;

    /// <summary>
    /// 给圆角条找一个"不压选区、不压胶囊"的位置。
    ///
    /// 用户要的锚法（第三轮修正）：**圆角条锚在脏区顶部那一条边上**，往屏幕中心那一侧偏 ——
    ///   脏区靠左（选区中心在屏幕左半）→ 条在脏区右上（贴顶、靠右）；
    ///   脏区靠右 → 条在脏区左上（贴顶、靠左）。
    /// 这样条始终和选区顶边同高、贴着选区的"内侧"边角，而不是跟胶囊竖直居中对齐。
    ///
    /// 只有当顶部这条边真放不下（选区顶贴屏、或两侧角没位置）时，才退到
    /// 左右空白 / 胶囊下方 / 屏幕四角等候选，最后再"重叠面积最小"兜底。
    /// </summary>
    private Rectangle PlaceRadiusBar(int edge)
    {
        var sel = Selection;
        var screen = _virtualBounds;
        int w = Math.Min(BarW, Math.Max(200, screen.Width - edge * 2));
        int h = BarH;

        Rectangle Clamp(Rectangle r) => new(
            Math.Clamp(r.X, screen.Left + edge, Math.Max(screen.Left + edge, screen.Right - r.Width - edge)),
            Math.Clamp(r.Y, screen.Top + edge, Math.Max(screen.Top + edge, screen.Bottom - r.Height - edge)),
            r.Width, r.Height);

        var barVisible = _bar.Visible && !_bar.Collapsed;

        bool Fits(Rectangle r) =>
            r.Left >= screen.Left + edge && r.Right <= screen.Right - edge &&
            r.Top >= screen.Top + edge && r.Bottom <= screen.Bottom - edge &&
            !r.IntersectsWith(sel) && !r.IntersectsWith(_pillScreen) &&
            // 功能条铺开时它的位置是禁区（它随时会被重新画出来）
            !(barVisible && r.IntersectsWith(_bar.Bounds));

        // 脏区靠左还是靠右：以选区中心在屏幕中心的哪一侧为准。
        bool selLeft = sel.Left + sel.Width / 2 < screen.Left + screen.Width / 2;

        // 顶部这条边的高度（贴选区顶、留 10px 间距）。
        int topY = sel.Top - h - 10;

        var candidates = new List<(string Name, Rectangle Rect)>();

        // ---- 首选：锚在脏区顶部、往屏幕中心那一侧偏 ----
        // 脏区靠左 → 条贴在脏区右上（右对齐选区右缘）；脏区靠右 → 条贴在脏区左上（左对齐选区左缘）。
        // 只有当顶部这条边放得下时才作候选（贴屏顶时硬塞会挤进标题栏）。
        if (sel.Top - screen.Top >= h + 24)
        {
            if (selLeft)
            {
                candidates.Add(("top-right", new Rectangle(sel.Right - w, topY, w, h)));
                candidates.Add(("top-left",  new Rectangle(sel.Left, topY, w, h)));
            }
            else
            {
                candidates.Add(("top-left",  new Rectangle(sel.Left, topY, w, h)));
                candidates.Add(("top-right", new Rectangle(sel.Right - w, topY, w, h)));
            }
        }

        // ---- 次级：贴顶放不下时，退到顶部边角外侧 / 左右空白 / 胶囊下方 / 屏幕角 ----
        // 圆角条必须**紧跟脏区（选区）**：横向锚点只用选区的边，不掺胶囊。
        // 之前用 max(sel.Right, pill.Right) —— 选区比胶囊（595）窄时胶囊会探出选区，
        // 锚点一下跳到胶囊外缘，圆角条脱离选区，视觉上就是"脏区低于 600 条停止跟随"。
        // 与胶囊重叠的候选会被 Fits() 里的 !IntersectsWith(_pillScreen) 跳过，不会真压胶囊。
        int rightEdge = sel.Right;
        int leftEdge = sel.Left;

        candidates.AddRange(new[]
        {
            // 顶部这条边放不下（贴屏顶）时，仍尽量靠选区顶部：放在选区顶边那一侧的外侧
            ("corner-right", new Rectangle(rightEdge + 10, sel.Top, w, h)),
            ("corner-left",  new Rectangle(leftEdge - w - 10, sel.Top, w, h)),

            // 左右空白区（与胶囊竖直居中对齐）
            ("right-pill",   new Rectangle(rightEdge + 10, _pillScreen.Top + (_pillScreen.Height - h) / 2, w, h)),
            ("left-pill",    new Rectangle(leftEdge - w - 10, _pillScreen.Top + (_pillScreen.Height - h) / 2, w, h)),

            // 胶囊下方（与前两级同列，像第三行工具）
            ("pill-below",   new Rectangle(_pillScreen.Left, _pillScreen.Bottom + 8, w, h)),

            // 屏幕四角的空白带（选区铺在某一角时，对角那片一定是空的）
            ("screen-tl",    new Rectangle(screen.Left + edge, screen.Top + edge, w, h)),
            ("screen-tr",    new Rectangle(screen.Right - w - edge, screen.Top + edge, w, h)),
            ("screen-bl",    new Rectangle(screen.Left + edge, screen.Bottom - h - edge, w, h)),
            ("screen-br",    new Rectangle(screen.Right - w - edge, screen.Bottom - h - edge, w, h)),
        });

        // 注意：刻意没有"胶囊上方"的候选 —— 圆角条压在工具胶囊头上是两个控件叠成
        // 一坨，用户明确不要（上一轮的需求 3）。

        foreach (var (name, rect) in candidates)
        {
            var r = Clamp(rect);
            if (!Fits(r)) continue;
            return CommitBar(r, name);
        }

        // 兜底：选区几乎占满整块屏幕 —— 选"压在选区和胶囊上最少"的那一个，
        // 而不是无条件回到选区上方（那会被夹到屏幕角上，看着像跑丢了）。
        Rectangle best = Clamp(candidates[0].Rect);
        string bestName = "clamp";
        long bestCost = long.MaxValue;

        foreach (var (name, rect) in candidates)
        {
            var r = Clamp(rect);
            long cost = OverlapArea(r, sel) * 1000L + OverlapArea(r, _pillScreen) * 600L;
            if (barVisible) cost += OverlapArea(r, _bar.Bounds) * 400L;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = r;
                bestName = name + "/clamp";
            }
        }

        return CommitBar(best, bestName);
    }

    private Rectangle CommitBar(Rectangle r, string name)
    {
        Log.Info($"[chrome] 选区={Selection.X},{Selection.Y},{Selection.Width},{Selection.Height} " +
                 $"胶囊={_pillScreen.X},{_pillScreen.Y},{_pillScreen.Width},{_pillScreen.Height} " +
                 $"圆角条={r.X},{r.Y},{r.Width},{r.Height} 策略={name}");
        return r;
    }

    private static long OverlapArea(Rectangle a, Rectangle b)
    {
        if (b.Width <= 0 || b.Height <= 0) return 0;
        var i = Rectangle.Intersect(a, b);
        return i.Width <= 0 || i.Height <= 0 ? 0 : (long)i.Width * i.Height;
    }

    private void HideChrome()
    {
        _pillScreen = Rectangle.Empty;
        RadiusBarScreen = Rectangle.Empty;
        _pillHover = -1;
    }

    // ---------------- 顶部功能条（参考图二） ----------------

    /// <summary>
    /// 功能条什么时候收起（其余时候常显）：
    ///   1) 正在框选或拉伸脏区 —— 它挡着正在操作的画面（需求 9）；
    ///      移动整块脏区时不算，否则一按一松它就闪一下；
    ///   2) 脏区已经框出来了 —— 截图界面进入"出图"阶段，条不该再露脸；
    ///   3) 用户勾了圆圈选择框（收起成小柄）；
    ///   4) 它和工具栏挤在一起。
    /// 只改状态，不自己触发重绘（由 <see cref="Redraw"/> 统一算脏区）。
    /// </summary>
    private void SyncBarVisibility()
    {
        bool selecting = _dragging && _grip != Grip.Move;
        bool hasSelection = Selection.Width > 8 && Selection.Height > 8;
        bool collide = _pillScreen.Width > 0 && _bar.Bounds.IntersectsWith(_pillScreen);
        bool visible = !selecting && !hasSelection && !collide;

        // 收起态仍然"可见"（要留那个小柄），铺开态才真正画整条
        if (_bar.Visible == visible)
        {
            if (!visible) _barDrag = false;
            return;
        }

        _bar.Visible = visible;
        if (!visible)
        {
            _barDrag = false;
            _bar.MenuOpen = false;
        }
    }

    private void OnBarButton(QuickBar.Btn btn)
    {
        switch (btn)
        {
            // 「截图」= 下拉菜单：自由 / 窗口 / 全屏
            case QuickBar.Btn.Capture:
                _bar.MenuOpen = !_bar.MenuOpen;
                _bar.PointerLeave();
                Log.Info($"[bar] 截图菜单 {(_bar.MenuOpen ? "展开" : "收起")} 位置={_bar.MenuBounds}");
                Redraw();
                break;

            case QuickBar.Btn.Record:
                // 录制需要"重新框一次区域"，所以先结束本次截图会话，
                // 由 AppContext 看到 RecordRequested 后接着开录制流程。
                Log.Info("[bar] 录屏 → 结束截图会话并进入录制");
                RecordRequested = true;
                Finish();
                break;

            case QuickBar.Btn.LongShot:
                // 长截图需要滚动拼接，当前版本没有实现：明确说清楚，不给假按钮。
                _bar.SetStatus("长截图暂未支持", 2000);
                Log.Info("[bar] 长截图：未实现");
                break;

            // 圆圈现在是选择框：点它只把功能条自己收起来（不动光标下的窗口）
            case QuickBar.Btn.HideBar:
                _bar.Collapsed = true;
                _bar.MenuOpen = false;
                _barDrag = false;
                Log.Info("[bar] 选择框勾选 → 隐藏功能条");
                Redraw();
                break;
        }
    }

    private void CloseBarMenu()
    {
        if (!_bar.MenuOpen) return;
        _bar.MenuOpen = false;
        _bar.PointerLeave();
        Redraw();
    }

    /// <summary>功能条下拉菜单：切换截图方式。</summary>
    private void OnBarMenu(QuickBar.Menu item)
    {
        Log.Info($"[bar] 菜单选择 {QuickBar.MenuLabel((int)item)}");
        _bar.CurrentMode = item;

        switch (item)
        {
            case QuickBar.Menu.Region:
                _mode = CaptureMode.Region;
                Selection = Rectangle.Empty;
                HideChrome();
                UpdateMagnifier();
                UpdateCursor(_current);
                Redraw();
                break;

            case QuickBar.Menu.Window:
                _mode = CaptureMode.Window;
                Selection = Rectangle.Empty;
                HideChrome();
                UpdateHoverWindow();
                UpdateCursor(_current);
                Redraw();
                break;

            case QuickBar.Menu.Fullscreen:
                // 整块虚拟桌面 = 脏区，直接出图（含圆角/阴影/保存策略）
                Selection = _virtualBounds;
                Log.Info($"[bar] 全屏截图 选区={Selection}");
                Finish();
                break;
        }
    }

    /// <summary>
    /// 「隐藏当前窗口」的旧实现（把光标下的窗口最小化后重新抓屏）。
    ///
    /// 只靠"遮罩层不画它"是没用的 —— 浮层底下本来就是快照，画面已经包含它了。
    /// 必须最小化之后重新抓屏，用户才能看到窗口后面的内容；
    /// 会话结束时统一还原（见 <see cref="RestoreHiddenWindows"/>），否则用户会以为窗口被关了。
    ///
    /// 现在那个圆圈已改成"收起功能条"的选择框，UI 上没有入口了；
    /// 逻辑本身是对的，保留着以便日后重新绑定或者做成设置项 ——
    /// 直接删掉的话，`_hiddenWindows` 那套还原机制也要一起拆。
    /// </summary>
    private void HideWindowUnderCursor()
    {
        Native.GetCursorPos(out var c);
        var pt = new Point(c.X, c.Y);

        var target = _hoverTarget;
        if (target == null)
        {
            var hwnd = Native.WindowFromPoint(new Native.POINT { X = pt.X, Y = pt.Y });
            var root = hwnd == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hwnd, Native.GA_ROOT);
            if (root != IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(root, out var pid);
                if (pid != (uint)Environment.ProcessId && ScreenCapture.TryGetWindowRect(root, out var rect))
                    target = new WindowTarget(root, rect, ScreenCapture.GetWindowTitle(root));
            }
        }

        if (target == null)
        {
            _bar.SetStatus("光标下没有可隐藏的窗口", 1800);
            return;
        }

        Native.ShowWindow(target.Handle, 6);      // SW_MINIMIZE
        _hiddenWindows.Add(target.Handle);
        Log.Info($"[bar] 隐藏窗口 \"{target.Title}\" hwnd=0x{target.Handle.ToInt64():X}");

        PumpMessages(220);                        // 等 DWM 真的把它撤下去
        Recapture();

        var name = target.Title.Length > 16 ? target.Title[..16] + "…" : target.Title;
        _bar.SetStatus("已隐藏：" + name, 2000);
    }

    /// <summary>重新抓一次整块桌面并刷新底图（隐藏窗口之后用）。</summary>
    private void Recapture()
    {
        var desktop = ScreenCapture.CaptureDesktop(_virtualBounds);
        var old = _snapshot;

        _snapshot = desktop.Image;
        _windows = desktop.Windows;
        _sampler = new SnapshotSampler(_snapshot);
        old?.Dispose();

        foreach (var f in _forms)
        {
            f.Snapshot = _snapshot;     // 触发压暗底图重建
            f.Render(null);
        }

        _lastHover = null;
        UpdateHoverWindow();
        UpdateMagnifier();
        Redraw();

        Log.Info($"[bar] 已重新抓屏 快照={_snapshot?.Width}x{_snapshot?.Height} 可选窗口={_windows.Count}");
    }

    /// <summary>还原被「隐藏当前窗口」最小化的窗口。</summary>
    private void RestoreHiddenWindows()
    {
        foreach (var hwnd in _hiddenWindows)
        {
            try
            {
                if (Native.IsWindow(hwnd)) Native.ShowWindow(hwnd, 9);   // SW_RESTORE
            }
            catch (Exception ex)
            {
                Log.Info("[bar] 还原窗口失败: " + ex.Message);
            }
        }
        _hiddenWindows.Clear();
    }

    private void OnPillActionClicked(PillAction action)
    {
        switch (action)
        {
            case PillAction.Close:
                Selection = Rectangle.Empty;
                _finished = true;
                break;

            // 胶囊上的保存/确认：这一下才是"真正截图"——
            // 收起遮罩 → 实时抓屏 → 圆角 → 直接写文件
            case PillAction.Confirm:
            case PillAction.Save:
                SaveRequested = true;
                Finish();
                break;

            // 撤销：把最后一笔移进"回收站"，重做能取回来
            case PillAction.Undo:
                if (_strokes.Count > 0)
                {
                    var last = _strokes[^1];
                    _strokes.RemoveAt(_strokes.Count - 1);
                    _redo.Add(last);
                }
                Redraw();
                break;

            // 重做：取回最近一次被撤销的笔画
            case PillAction.Redo:
                if (_redo.Count > 0)
                {
                    var back = _redo[^1];
                    _redo.RemoveAt(_redo.Count - 1);
                    _strokes.Add(back);
                }
                Redraw();
                break;

            // 钉在桌面：出图后直接钉出
            case PillAction.Pin:
                PinRequested = true;
                Finish();
                break;

            // 分享：出图后弹分享菜单
            case PillAction.Share:
                ShareRequested = true;
                Finish();
                break;

            // 识图 / 翻译：**留在截图界面里**跑 OCR。
            // 没有文字就只贴一条标签（不再弹空面板），翻译成功则把译文写回画面。
            case PillAction.Ocr:
                StartTextTask(translate: false);
                break;

            case PillAction.Translate:
                StartTextTask(translate: true);
                break;

            // 绘制类工具：就地切换 + 弹出次级胶囊，**不离开截图界面**
            default:
                ToggleFlyout(action);
                break;
        }
    }

    /// <summary>控制点命中半径（屏幕上好点，比视觉稍大）。</summary>
    private const int GripHit = 9;

    /// <summary>清空重做历史，并释放被丢弃笔画缓存的字体/中间位图。</summary>
    private void DropRedo()
    {
        foreach (var a in _redo) a.ReleaseCachedResources();
        _redo.Clear();
    }

    private static void PumpMessages(int totalMs)
    {
        var until = Environment.TickCount64 + totalMs;
        while (Environment.TickCount64 < until)
        {
            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(8);
        }
    }

    /// <summary>把标注按偏移平移到图片坐标。</summary>
    private static void ShiftBy(Core.Annotation a, float dx, float dy)
    {
        switch (a)
        {
            case Core.RectAnnotation r: r.Rect = Offset(r.Rect, dx, dy); break;
            case Core.EllipseAnnotation e: e.Rect = Offset(e.Rect, dx, dy); break;
            case Core.MosaicAnnotation m: m.Rect = Offset(m.Rect, dx, dy); break;
            case Core.BlurAnnotation b: b.Rect = Offset(b.Rect, dx, dy); break;
            case Core.LineAnnotation l: l.Start = new PointF(l.Start.X + dx, l.Start.Y + dy); l.End = new PointF(l.End.X + dx, l.End.Y + dy); break;
            case Core.ArrowAnnotation ar: ar.Start = new PointF(ar.Start.X + dx, ar.Start.Y + dy); ar.End = new PointF(ar.End.X + dx, ar.End.Y + dy); break;
            case Core.PenAnnotation p:
                for (int i = 0; i < p.Points.Count; i++) p.Points[i] = new PointF(p.Points[i].X + dx, p.Points[i].Y + dy);
                break;
            case Core.TextAnnotation t: t.Position = new PointF(t.Position.X + dx, t.Position.Y + dy); break;
            case Core.NumberAnnotation n: n.Position = new PointF(n.Position.X + dx, n.Position.Y + dy); break;

            // 译文原位替换：行框与"覆盖补丁"必须一起平移，否则背景还原会错位
            case Core.TranslationAnnotation tr:
                tr.Rect = Offset(tr.Rect, dx, dy);
                if (tr.SamplePatch.Width > 0 && tr.SamplePatch.Height > 0)
                    tr.SamplePatch = new Rectangle(
                        (int)Math.Round(tr.SamplePatch.X + dx),
                        (int)Math.Round(tr.SamplePatch.Y + dy),
                        tr.SamplePatch.Width, tr.SamplePatch.Height);
                break;
        }
    }

    private static RectangleF Offset(RectangleF r, float dx, float dy) => new(r.X + dx, r.Y + dy, r.Width, r.Height);

    /// <summary>保存瞬间从活屏抓取选区；失败返回 null。</summary>
    private static Bitmap? GrabLive(Rectangle screenRect)
    {
        try
        {
            var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(screenRect.Left, screenRect.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Info("[overlay] 实时抓屏失败，退回快照: " + ex.Message);
            return null;
        }
    }

    /// <summary>从会话快照里裁出选区（兜底路径）。</summary>
    private Bitmap CropSnapshot(Rectangle clipped)
    {
        var bmp = new Bitmap(clipped.Width, clipped.Height, PixelFormat.Format32bppArgb);
        if (_snapshot == null) return bmp;

        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_snapshot,
            new Rectangle(0, 0, clipped.Width, clipped.Height),
            new Rectangle(clipped.Left - _virtualBounds.Left, clipped.Top - _virtualBounds.Top,
                          clipped.Width, clipped.Height),
            GraphicsUnit.Pixel);
        return bmp;
    }

    /// <summary>把位图裁成圆角（抗锯齿 + 羽化），四角留透明。</summary>
    private static Bitmap ApplyRadius(Bitmap src, int radius) => Core.ImageRounder.Round(src, radius);


    /// <summary>选区八处控制点（四角 + 四边中点），屏幕坐标。</summary>
    public static IEnumerable<Point> GripPoints(Rectangle r)
    {
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        yield return new Point(r.Left, r.Top);
        yield return new Point(cx, r.Top);
        yield return new Point(r.Right, r.Top);
        yield return new Point(r.Right, cy);
        yield return new Point(r.Right, r.Bottom);
        yield return new Point(cx, r.Bottom);
        yield return new Point(r.Left, r.Bottom);
        yield return new Point(r.Left, cy);
    }

    /// <summary>命中了哪个控制点（优先角落，其次边中点，最后整体平移）。</summary>
    public static Grip GripAt(Rectangle r, Point p)
    {
        bool Near(Point q) => Math.Abs(p.X - q.X) <= GripHit && Math.Abs(p.Y - q.Y) <= GripHit;

        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;

        if (Near(new Point(r.Left, r.Top))) return Grip.TL;
        if (Near(new Point(r.Right, r.Top))) return Grip.TR;
        if (Near(new Point(r.Left, r.Bottom))) return Grip.BL;
        if (Near(new Point(r.Right, r.Bottom))) return Grip.BR;
        if (Near(new Point(cx, r.Top))) return Grip.T;
        if (Near(new Point(cx, r.Bottom))) return Grip.B;
        if (Near(new Point(r.Left, cy))) return Grip.L;
        if (Near(new Point(r.Right, cy))) return Grip.R;

        return r.Contains(p) ? Grip.Move : Grip.None;
    }

    private PointF _inkStart;

    /// <summary>建立一笔新标注（用 Annotation 类族，完整支持全部类型）。</summary>
    private Core.Annotation? NewStroke(Core.AnnotationKind tool, PointF p)
    {
        var draft = Ui.OverlayInk.New(tool, _inkColor, _inkWidth, _inkFill, _snapshot);
        if (draft == null) return null;

        switch (draft)
        {
            case Core.PenAnnotation pen: pen.Points.Add(p); break;
            case Core.NumberAnnotation num: num.Position = p; num.Index = _strokes.Count(x => x is Core.NumberAnnotation) + 1; break;
            case Core.TextAnnotation txt: txt.Position = p; txt.FontSize = _inkFontSize; break;
            case Core.MosaicAnnotation mosaic: mosaic.SourceOrigin = _virtualBounds.Location; break;
            case Core.BlurAnnotation blur: blur.SourceOrigin = _virtualBounds.Location; break;
        }
        return draft;
    }

    private static void ExtendStroke(Core.Annotation draft, PointF start, PointF p)
    {
        Ui.OverlayInk.Extend(draft, start, p);
    }

    /// <summary>按抓取点拖动后的新选区（夹在虚拟桌面内，最小 24×24）。</summary>
    private Rectangle ResizeBy(Rectangle start, Point from, Point to, Grip grip)
    {
        int dx = to.X - from.X, dy = to.Y - from.Y;
        int l = start.Left, t = start.Top, rr = start.Right, b = start.Bottom;

        switch (grip)
        {
            case Grip.L: l += dx; break;
            case Grip.R: rr += dx; break;
            case Grip.T: t += dy; break;
            case Grip.B: b += dy; break;
            case Grip.TL: l += dx; t += dy; break;
            case Grip.TR: rr += dx; t += dy; break;
            case Grip.BL: l += dx; b += dy; break;
            case Grip.BR: rr += dx; b += dy; break;
            case Grip.Move:
                int w = start.Width, h = start.Height;
                l = Math.Clamp(start.Left + dx, _virtualBounds.Left, _virtualBounds.Right - w);
                t = Math.Clamp(start.Top + dy, _virtualBounds.Top, _virtualBounds.Bottom - h);
                return new Rectangle(l, t, w, h);
        }

        l = Math.Clamp(l, _virtualBounds.Left, _virtualBounds.Right - 24);
        rr = Math.Clamp(rr, l + 24, _virtualBounds.Right);
        t = Math.Clamp(t, _virtualBounds.Top, _virtualBounds.Bottom - 24);
        b = Math.Clamp(b, t + 24, _virtualBounds.Bottom);

        Radius = Math.Min(Radius, Math.Min(rr - l, b - t) / 2);
        return Rectangle.FromLTRB(l, t, rr, b);
    }
    /// <summary>文字标签：弹输入框，完成后写入标注。</summary>
    private void BeginTextInput(Point p)
    {
        var pos = new PointF(p.X, p.Y);
        using var input = new TextInputForm(p, _inkColor, _inkFontSize);
        input.ShowDialog();

        if (input.Result is { } text)
        {
            DropRedo();
            _strokes.Add(new Core.TextAnnotation
            {
                Kind = Core.AnnotationKind.Text,
                Position = pos,
                Text = text,
                Stroke = _inkColor,
                FontSize = _inkFontSize
            });
            Redraw();
        }
    }

    /// <summary>切到某标注工具（同步更新次级胶囊高亮）。</summary>
    private void SetTool(Core.AnnotationKind kind)
    {
        Tool = kind;
        Log.Info($"[tool] active={kind}");   // 真机脚本据此断言"点了哪个工具"
        Redraw();
    }

    /// <summary>弹出/收起次级胶囊（ToolFlyout）。</summary>
    private void ToggleFlyout(PillAction owner)
    {
        if (_flyVisible && _flyOwner == owner)
        {
            HideFlyout();
            return;
        }

        _flyVisible = true;
        _flyOwner = owner;
        var kind = PillActionToKind(owner) ?? Core.AnnotationKind.Rectangle;
        SetTool(kind);

        _fly ??= CreateFlyout();

        switch (owner)
        {
            case PillAction.Shape:
                _fly.Configure(
                    new[]
                    {
                        new ToolFlyout.ToolButton(Core.AnnotationKind.Rectangle, IconKind.Rectangle),
                        new ToolFlyout.ToolButton(Core.AnnotationKind.Ellipse, IconKind.Ellipse),
                        new ToolFlyout.ToolButton(Core.AnnotationKind.Line, IconKind.Line),
                    },
                    kind, showFill: true, _inkFill,
                    "粗细", 1, 24, (int)Math.Round(_inkWidth), _inkColor);
                break;

            case PillAction.Arrow:
                _fly.Configure(null, kind, false, false,
                    "粗细", 1, 24, (int)Math.Round(_inkWidth), _inkColor);
                break;

            case PillAction.Pen:
                _fly.Configure(null, kind, false, false,
                    "粗细", 1, 24, (int)Math.Round(_inkWidth), _inkColor);
                break;

            case PillAction.Text:
                _fly.Configure(
                    new[]
                    {
                        new ToolFlyout.ToolButton(Core.AnnotationKind.Text, IconKind.Text),
                        new ToolFlyout.ToolButton(Core.AnnotationKind.Number, IconKind.Number),
                    },
                    kind, false, false,
                    "大小", 12, 72, (int)Math.Round(_inkFontSize), _inkColor);
                break;

            case PillAction.Stipple:
                _fly.Configure(
                    new[]
                    {
                        new ToolFlyout.ToolButton(Core.AnnotationKind.Mosaic, IconKind.Mosaic),
                        new ToolFlyout.ToolButton(Core.AnnotationKind.Blur, IconKind.Blur),
                    },
                    kind, false, false,
                    "粗细", 1, 24, (int)Math.Round(_inkWidth), _inkColor);
                break;

            default:
                HideFlyout();
                return;
        }

        // 次级胶囊定位：头部与主胶囊左边缘对齐，宽度**固定**（需求 10：
        // 胶囊不随截图大小变化）。以前取"主胶囊一半"，主胶囊一变宽窄它就跟着抖。
        int flyW = Math.Max(FlyoutWidth, _fly.MinimumWidth);
        flyW = Math.Min(flyW, Math.Max(240, _virtualBounds.Width - 16));
        var flySize = new Size(flyW, PillToolbar.BaseSize.Height);
        const int gap = 8;

        int x = _pillScreen.Left;
        x = Math.Clamp(x, _virtualBounds.Left + 8,
            Math.Max(_virtualBounds.Left + 8, _virtualBounds.Right - flySize.Width - 8));

        int yBelow = _pillScreen.Bottom + gap;
        int y = yBelow + flySize.Height <= _virtualBounds.Bottom - 6
            ? yBelow
            : Math.Max(_virtualBounds.Top + 6, _pillScreen.Top - flySize.Height - gap);

        _fly.Place(new Point(x, y), flySize);
        Redraw();
    }

    private void HideFlyout()
    {
        if (!_flyVisible) return;
        _flyVisible = false;
        _fly?.PointerLeave();
        DismissPopup();
        Redraw();
    }

    /// <summary>次级胶囊宽度：固定值（不随选区/主胶囊缩放）。</summary>
    private const int FlyoutWidth = 300;

    private ToolFlyout CreateFlyout()
    {
        var fly = new ToolFlyout();
        fly.ToolPicked += kind => SetTool(kind);
        fly.SliderChanged += v =>
        {
            if (_flyOwner == PillAction.Text) _inkFontSize = v;
            else _inkWidth = v;
            Redraw();
        };
        fly.FillToggled += filled => { _inkFill = filled; Redraw(); };
        fly.SwatchClicked += ShowColorPicker;
        fly.Changed += OnChromeChanged;
        return fly;
    }

    private void ShowColorPicker()
    {
        DismissPopup();

        // 高度固定（与主胶囊一致）：色板卡片不会比旁边的次级胶囊高出一截
        var avoid = _flyVisible && _fly != null ? _fly.Bounds : _pillScreen;
        int capsuleH = PillToolbar.BaseSize.Height;

        _popup = new ColorPickerChrome(_inkColor, c =>
        {
            _inkColor = c;
            _fly?.SetSwatch(c);
            DismissPopup();
            Redraw();
        }, capsuleH);
        _popup.Changed += OnChromeChanged;
        _popup.CustomColorRequested += ShowCustomColorDialog;

        var screen = _virtualBounds;
        Log.Info($"[picker] anchor={avoid.Right + 8},{avoid.Top} " +
                 $"fly={(_flyVisible && _fly != null ? _fly.Bounds.ToString() : "无")} " +
                 $"virtual={screen.X},{screen.Y},{screen.Width},{screen.Height}");

        // 优先落在次级胶囊下方、与主胶囊同一列起点对齐（三级控件同列）
        _popup.Place(avoid, screen, _pillScreen, _pillScreen.IsEmpty ? int.MinValue : _pillScreen.Left);
        Redraw();
    }

    private void ShowCustomColorDialog()
    {
        // 这里正处在全局低级鼠标钩子的回调里。直接 ShowDialog 会开一个嵌套消息泵，
        // 钩子在同一线程重入 —— 取色框上的每次点击都会被浮层当成选区操作。
        // 真机表现（ocrgui picker）：点"自定义…"后在取色框里点一下，
        // 选区被改成鼠标下那个窗口的尺寸，截图会话直接结束。
        // 所以先把输入封掉，再把弹窗投递到浮层消息队列，等本轮回调返回后再弹。
        var owner = _forms.FirstOrDefault();
        if (owner == null || _modalOpen) return;

        _modalOpen = true;
        _dragging = false;
        _grip = Grip.None;
        _radiusDrag = false;
        _inkActive = false;
        _active = null;
        _downConsumed = false;

        try
        {
            owner.BeginInvoke(new Action(() => RunCustomColorDialog(owner)));
        }
        catch (Exception ex)
        {
            Log.Info("[overlay] 自定义颜色框投递失败: " + ex.Message);
            _modalOpen = false;
        }
    }

    private void RunCustomColorDialog(Form owner)
    {
        try
        {
            using var dlg = new ColorDialog
            {
                FullOpen = true,
                Color = _inkColor,
                AnyColor = true
            };
            // 必须以浮层为所有者：否则模态框会落在全屏置顶浮层下面看不见。
            if (dlg.ShowDialog(owner) == DialogResult.OK)
            {
                _inkColor = dlg.Color;
                _fly?.SetSwatch(dlg.Color);
                DismissPopup();
                Log.Info($"[picker] 自定义颜色确认 #{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
            }
            else
            {
                Log.Info("[picker] 自定义颜色取消");
            }
            Redraw();
        }
        catch (Exception ex)
        {
            Log.Info("[overlay] 自定义颜色框失败: " + ex.Message);
        }
        finally
        {
            // 模态期间积压的鼠标事件全部丢弃；用当前光标位置重置基准，
            // 并把"上一次按下"的时间戳推到当下，避免关框那一下的残留被当成新一次拖拽起点。
            Native.GetCursorPos(out var c);
            _current = new Point(c.X, c.Y);
            _lastDownTick = Environment.TickCount64;
            _modalOpen = false;
        }
    }

    private void DismissPopup()
    {
        if (_popup == null) return;
        _popup.Changed -= OnChromeChanged;
        _popup.CustomColorRequested -= ShowCustomColorDialog;
        _popup = null;
        Redraw();
    }

    // ---------------- 识图 / 翻译（在截图界面内完成） ----------------
    //
    // 两个格位以前都是"先结束会话 → 出图 → 弹结果面板，再识别/翻译"：
    //   · 面板没有 owner、Manual 又没设坐标，直接落在**屏幕左上角**；
    //   · 截图里没有文字时，还会弹一个写着「未识别到文字」的空面板；
    //   · 翻译永远只把译文塞进面板，图上一个字都没变。
    // 现在改成在会话内跑 OCR，两个格位走**同一条写回路径**：
    //   · 没有文字 → 不弹任何窗口，只在对应格位上贴一条提示标签；
    //   · 识图成功 → 原文被采样背景填掉，按原字号重写识别结果，并复制到剪贴板；
    //   · 翻译成功 → 同一条路径，写入译文；
    //   两者都不结束会话，照常保存/钉/分享。

    /// <summary>OCR/翻译任务进行中（防止连点重复起任务）。</summary>
    private bool _textBusy;

    /// <summary>贴在某个胶囊格位上的状态标签（识别中/未识别到文字/已翻译…）。</summary>
    private string? _statusText;
    private PillAction _statusOwner = PillAction.None;
    private long _statusUntil;

    private System.Windows.Forms.Timer? _statusTimer;

    private void SetPillStatus(PillAction owner, string text, int holdMs)
    {
        _statusOwner = owner;
        _statusText = text;
        _statusUntil = Environment.TickCount64 + Math.Max(400, holdMs);

        if (_statusTimer == null)
        {
            _statusTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _statusTimer.Tick += (_, _) =>
            {
                if (_statusText == null) { _statusTimer?.Stop(); return; }
                if (Environment.TickCount64 >= _statusUntil) ClearPillStatus();
            };
        }

        _statusTimer.Stop();
        _statusTimer.Start();
        Redraw();
    }

    private void ClearPillStatus()
    {
        _statusTimer?.Stop();
        if (_statusText == null) return;
        _statusText = null;
        _statusOwner = PillAction.None;
        Redraw();
    }

    /// <summary>把回调投递回浮层线程（OCR 在后台跑，结果必须回到 UI 线程改状态）。</summary>
    private void PostToOverlay(Action action)
    {
        var owner = _forms.FirstOrDefault();
        if (owner == null || _finished) return;
        try
        {
            if (owner.IsHandleCreated && !owner.IsDisposed) owner.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            Log.Info("[text] 结果投递失败（会话可能已关闭）: " + ex.Message);
        }
    }

    private void StartTextTask(bool translate)
    {
        if (_textBusy) return;

        var clipped = Rectangle.Intersect(Selection, _virtualBounds);
        if (clipped.Width < 8 || clipped.Height < 8 || _snapshot == null)
        {
            SetPillStatus(translate ? PillAction.Translate : PillAction.Ocr, "选区太小", 2200);
            return;
        }

        _textBusy = true;
        SetPillStatus(translate ? PillAction.Translate : PillAction.Ocr,
            translate ? "翻译中…" : "识别中…", 120000);

        Bitmap crop;
        try
        {
            // 识别用会话快照裁出的选区图（画面与最终出图一致，且不含遮罩/胶囊）
            crop = CropSnapshot(clipped);
        }
        catch (Exception ex)
        {
            _textBusy = false;
            Log.Info("[text] 裁剪选区失败: " + ex.Message);
            SetPillStatus(translate ? PillAction.Translate : PillAction.Ocr, "识别失败", 2600);
            return;
        }

        var settings = _settings;
        Log.Info($"[text] 开始{(translate ? "翻译" : "识别")} 选区={clipped}");

        _ = Task.Run(async () =>
        {
            IReadOnlyList<OcrLine> lines = Array.Empty<OcrLine>();
            string[]? translated = null;
            string? error = null;

            try
            {
                lines = await OcrService.RecognizeLinesAsync(crop);
                if (translate && lines.Count > 0)
                {
                    var source = lines.Select(l => l.Text).ToList();
                    translated = await TranslateService.TranslateLinesAsync(source, settings);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                crop.Dispose();
            }

            PostToOverlay(() => OnTextTaskDone(translate, lines, translated, error, clipped));
        });
    }

    private void OnTextTaskDone(bool translate, IReadOnlyList<OcrLine> lines, string[]? translated,
        string? error, Rectangle clipped)
    {
        _textBusy = false;

        // 会话已经结束（用户在识别途中保存/取消）：标签无处可贴，结果也不再需要
        if (_finished) return;

        var owner = translate ? PillAction.Translate : PillAction.Ocr;

        if (error != null)
        {
            Log.Info($"[text] {(translate ? "翻译" : "识别")}失败: {error}");
            SetPillStatus(owner, translate ? "翻译失败" : "识别失败", 3000);
            return;
        }

        if (lines.Count == 0)
        {
            // 这是用户得到的**唯一**反馈（没有面板、没有弹窗），所以停留久一点
            Log.Info($"[text] {(translate ? "翻译" : "识别")}：选区里没有文字，停在截图界面并贴标签");
            SetPillStatus(owner, "未识别到文字", 4500);
            return;
        }

        // 识图不再"出图 + 弹文字面板"：识别结果和译文走同一条路 ——
        // 原位写回画面（原文被采样背景填掉，再按原字号重写一遍）。
        // 用户看到的始终是截图本身，左上角也不会再冒出一个结果窗口。
        string[] target;
        if (translate)
        {
            // 译文与原文逐行相同 → 公共接口没生效（翻译服务静默回退原文），
            // 这时把原文重画一遍等于撒谎，只在格位上提示失败。
            var src = lines.Select(l => l.Text).ToList();
            int changed = 0;
            for (int i = 0; i < src.Count && translated != null && i < translated.Length; i++)
            {
                var dst = translated[i]?.Trim();
                if (!string.IsNullOrWhiteSpace(dst) &&
                    !string.Equals(src[i].Trim(), dst, StringComparison.OrdinalIgnoreCase))
                    changed++;
            }

            if (changed == 0)
            {
                Log.Info("[text] 译文与原文完全一致，判定翻译未生效");
                SetPillStatus(owner, "翻译失败", 3000);
                return;
            }

            target = translated!;
        }
        else
        {
            target = lines.Select(l => l.Text).ToArray();
        }

        int placed = ApplyTranslations(lines, target, clipped);
        if (translate) TranslateApplied = placed > 0;

        // 识图不再弹面板，那"把文字取走"的出口就只剩剪贴板了：
        // 顺手复制一份，用户 Ctrl+V 就能用，标签上也说清楚。
        bool copied = false;
        if (!translate && placed > 0)
        {
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lines.Select(l => l.Text)));
                copied = true;
            }
            catch (Exception ex)
            {
                Log.Info("[text] 复制到剪贴板失败: " + ex.Message);
            }
        }

        if (translate)
            Log.Info($"[text] 已把 {placed} 行译文写回选区画面（共 {lines.Count} 行）");
        else
            Log.Info($"[text] 识别成功 {lines.Count} 行，已写回选区画面（共 {placed} 行）" +
                     $"{(copied ? "，并已复制到剪贴板" : "")}");

        SetPillStatus(owner,
            placed > 0
                ? (translate ? $"已翻译 {placed} 行" : $"已提取 {placed} 行{(copied ? " · 已复制" : "")}")
                : "处理失败",
            3200);
        Redraw();
    }

    /// <summary>
    /// 把译文写成"原位替换"标注：先按行框周边采样背景（把原文擦干净），
    /// 再在原行框里写译文。标注存屏幕坐标，出图时由 <see cref="OverlayInk"/> 平移回图片坐标。
    /// </summary>
    private int ApplyTranslations(IReadOnlyList<OcrLine> lines, string[] translated, Rectangle clipped)
    {
        // 重复翻译前先摘掉上一批，避免新旧译文叠在一起
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            if (_strokes[i] is not Core.TranslationAnnotation old) continue;
            _strokes.RemoveAt(i);
            old.ReleaseCachedResources();
        }

        int added = 0;
        try
        {
            using var sheet = CropSnapshot(clipped);   // 采样用：与 OCR 同一张图
            for (int i = 0; i < lines.Count; i++)
            {
                var text = i < translated.Length ? translated[i]?.Trim() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;

                var box = lines[i].Bounds;
                if (box.Width < 2 || box.Height < 2) continue;

                Log.Info($"[text] 行{i} {box} \"{lines[i].Text}\" → \"{text}\"");

                int padX = Math.Clamp((int)Math.Round(box.Height * 0.12), 2, 8);
                int padY = Math.Clamp((int)Math.Round(box.Height * 0.10), 1, 3);
                var bd = TextBackdrop.Sample(sheet, box, padX, padY);

                _strokes.Add(new Core.TranslationAnnotation
                {
                    Kind = Core.AnnotationKind.Text,
                    Rect = new RectangleF(clipped.Left + box.X, clipped.Top + box.Y, box.Width, box.Height),
                    Text = text!,
                    SourceText = lines[i].Text,
                    InkHeight = bd?.InkHeight ?? 0,
                    SamplePatch = ToScreen(bd?.Patch, clipped),
                    PadX = padX,
                    PadY = padY,
                    BackgroundColor = bd?.MainBackground ?? Color.White,
                    TextColor = bd?.TextColor ?? Color.FromArgb(24, 28, 36),
                    RowLeft = bd?.RowLeft,
                    RowRight = bd?.RowRight
                });
                added++;
            }
        }
        catch (Exception ex)
        {
            Log.Info("[text] 译文渲染准备失败: " + ex.Message);
        }

        if (added > 0) DropRedo();
        return added;
    }

    /// <summary>把图片坐标的矩形平移到屏幕坐标（空矩形原样返回）。</summary>
    private static Rectangle ToScreen(Rectangle? r, Rectangle origin) =>
        r is { Width: > 0, Height: > 0 } v
            ? new Rectangle(origin.Left + v.X, origin.Top + v.Y, v.Width, v.Height)
            : Rectangle.Empty;

    /// <summary>胶囊动作 → 标注类型。</summary>
    private static Core.AnnotationKind? PillActionToKind(PillAction action) => action switch
    {
        PillAction.Shape => Core.AnnotationKind.Rectangle,
        PillAction.Arrow => Core.AnnotationKind.Arrow,
        PillAction.Pen => Core.AnnotationKind.Pen,
        PillAction.Text => Core.AnnotationKind.Text,
        PillAction.Stipple => Core.AnnotationKind.Mosaic,
        _ => null
    };

    /// <summary>圆角滑杆拖动：按滑杆上的 x 换算成半径。</summary>
    private void SetRadiusFromX(int x, Rectangle slider)
    {
        float t = Math.Clamp((x - slider.Left) / (float)Math.Max(1, slider.Width), 0f, 1f);
        int limit = Math.Min(Math.Min(Selection.Width, Selection.Height) / 2, 400);
        Radius = (int)Math.Round(t * limit);
        Redraw();
    }

    /// <summary>
    /// 圆角工具条内部几何：滑杆 / 阴影勾选框。
    ///
    /// 条改成 380×34（原 324×24 太细，8pt 字挤在一条缝里看不清），
    /// 内部按新的宽度重排：尺寸 18..、圆角 116..、滑杆 158..262、数值 276..、
    /// 阴影右对齐到勾选框左侧 10px、勾选框 15px 且右距 20。
    /// </summary>
    public static (Rectangle slider, Rectangle shadowBox, Rectangle confirm) ToolbarParts(Rectangle bar)
    {
        var slider = new Rectangle(bar.Left + 158, bar.Top + bar.Height / 2 - 2, 104, 4);
        var shadowBox = new Rectangle(bar.Right - 35, bar.Top + (bar.Height - 15) / 2, 15, 15);
        return (slider, shadowBox, Rectangle.Empty);
    }

    /// <summary>点圆角工具条：调圆角 / 切阴影。返回 true 表示事件被消费。</summary>
    private bool HandleToolbarClick(Point p)
    {
        var bar = RadiusBarScreen;
        if (bar.IsEmpty || !bar.Contains(p)) return false;

        var (slider, shadowBox, _) = ToolbarParts(bar);

        var hitSlider = slider;
        hitSlider.Inflate(0, 10);
        if (hitSlider.Contains(p))
        {
            _radiusDrag = true;
            _radiusSlider = slider;
            SetRadiusFromX(p.X, slider);
            return true;
        }

        if (shadowBox.Contains(p))
        {
            ShadowEnabled = !ShadowEnabled;
            Redraw();
            return true;
        }

        return true;
    }

    private void OnPointerUp(Point p)
    {
        if (_finished || _modalOpen) return;
        _downConsumed = false;

        // 拖动功能条结束
        if (_barDrag)
        {
            _barDrag = false;
            Log.Info($"[bar] 拖动结束 位置={_bar.Bounds}");
            UpdateCursor(p);
            Redraw();
            return;
        }

        // 次级胶囊的滑块拖动结束（同层手绘控件）
        _fly?.PointerUp();

        if (_active != null)
        {
            // 不要再按"点数 < 3 且时间 < 400ms"判为假事件直接 return：
            // 快速两笔时第二笔会被卡住，_active/_inkActive 无法复位，
            // 后续 400ms 内既不能下笔也不能重新框选。
            // 重复的 Up 本身就是幂等的（第一次已经把 _active 置空），
            // 笔画是否有效交给 IsValid 判定即可。
            if (!Ui.OverlayInk.IsValid(_active))
            {
                _strokes.Remove(_active);
                _active.ReleaseCachedResources();
            }
            _active = null;
            _inkActive = false;
            Redraw();
            return;
        }

        if (_radiusDrag)
        {
            _radiusDrag = false;
            _radiusSlider = Rectangle.Empty;
            return;
        }

        if (!_dragging) return;

        // 拖动控制点/整体平移：收尾即可，选区保持不变
        if (_grip != Grip.None)
        {
            _dragging = false;
            _grip = Grip.None;
            ApplySnap();
            UpdateMagnifier();
            LayoutChrome();          // 松手时再排一次（拖动中途的重排可能被夹取过）
            UpdateCursor(p);
            Redraw();
            return;
        }

        _dragging = false;
        _current = p;
        Selection = Normalize(_start, _current);

        if (Selection.Width < 3 || Selection.Height < 3)
        {
            UpdateHoverWindow();

            // 单击窗口 = 把这个窗口整块设为脏区（自动识别窗口）。
            // 不结束会话：用户接着可以标注/识图/保存，和拖框选出来的选区一样。
            if (_hoverTarget is { } target)
            {
                Selection = Rectangle.Intersect(target.Bounds, _virtualBounds);
                _snapHint = null;
                Log.Info($"[window] 单击选中窗口 \"{target.Title}\" → " +
                         $"{Selection.X},{Selection.Y},{Selection.Width},{Selection.Height}");

                if (_mode == CaptureMode.Window)
                {
                    Finish();
                    return;
                }

                UpdateMagnifier();
                LayoutChrome();
                UpdateCursor(p);
                Redraw();
                return;
            }

            // 空白处单击：保持会话（Esc 才是取消）。旧版这里直接结束会话，
            // 于是"点一下桌面看看"就把整个截图界面关掉了。
            Selection = Rectangle.Empty;
            UpdateMagnifier();
            UpdateCursor(p);
            Redraw();
            return;
        }

        ApplySnap();
        _snapHint = null;

        UpdateMagnifier();
        LayoutChrome();
        UpdateCursor(p);
        Redraw();
    }

    private void Finish()
    {
        HideChrome();
        HideFlyout();
        DismissPopup();

        // 功能条属于"准备截图"的界面，出图/取消之后不该留下
        // —— 这里顺手把它的矩形立刻重画一遍，屏幕上不会残留一帧
        EraseBar();

        // 标签属于"截图界面上的提示"，会话结束后没有可贴的地方
        _statusTimer?.Stop();
        _statusText = null;
        _statusOwner = PillAction.None;

        _finished = true;
    }

    /// <summary>把功能条（含下拉菜单、收起小柄）从画面上擦掉并立即提交一帧。</summary>
    private void EraseBar()
    {
        var rect = _bar.DirtyBounds;
        bool wasVisible = _bar.Visible;

        _bar.Visible = false;
        _bar.MenuOpen = false;
        _bar.Collapsed = false;
        _barDrag = false;

        if (!wasVisible || rect.IsEmpty || _forms.Count == 0) return;

        foreach (var f in _forms)
            f.Render(Rectangle.Inflate(rect, 14, 60));
    }

    private static Rectangle Normalize(Point a, Point b) => Rectangle.FromLTRB(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private void ApplySnap()
    {
        if (_windows.Count == 0) return;

        const int T = 12;
        var best = Rectangle.Empty;
        int bestDist = int.MaxValue;

        foreach (var target in _windows)
        {
            var w = target.Bounds;
            int dx = SnapDist(Selection.Left, w.Left, w.Right);
            int dy = SnapDist(Selection.Top, w.Top, w.Bottom);
            int d = dx + dy;
            if (d < bestDist && dx <= T && dy <= T)
            {
                bestDist = d;
                best = w;
            }
        }

        if (bestDist < int.MaxValue)
        {
            _snapHint = best;
            Selection = new Rectangle(
                Snap(Selection.Left, best.Left, best.Right),
                Snap(Selection.Top, best.Top, best.Bottom),
                Selection.Width, Selection.Height);
        }
    }

    private static int SnapDist(int v, int lo, int hi)
    {
        if (v < lo) return lo - v;
        if (v > hi) return v - hi;
        return 0;
    }

    private static int Snap(int v, int lo, int hi)
    {
        if (v < lo) return lo;
        if (v > hi) return hi;
        return v;
    }

    /// <summary>
    /// 光标下是哪个窗口。
    ///
    /// 列表来自 <see cref="ScreenCapture.EnumerateTargets"/>，**按 z 序从上到下**排好，
    /// 所以第一个包含光标的矩形就是"看起来在最上面的那个窗口"，
    /// 不需要自己比较堆叠顺序（用窗口面积排序会把全屏的浏览器误判成桌面）。
    ///
    /// 已经有选区时不再整窗高亮：那时鼠标是在调选区、画标注，再叠一层整窗框只会更乱。
    /// </summary>
    private void UpdateHoverWindow()
    {
        if (Selection.Width > 8 || _windows.Count == 0)
        {
            _hoverTarget = null;
            _hoverWindow = null;
            _hoverTitle = null;
            return;
        }

        Native.GetCursorPos(out var c);
        var pt = new Point(c.X, c.Y);

        WindowTarget? found = null;
        foreach (var w in _windows)
        {
            if (w.Bounds.Contains(pt)) { found = w; break; }
        }

        // 光标停在顶部功能条上时不提示窗口（条是自己的 UI，不是待截目标）
        if (found != null && _bar.Visible && _bar.Contains(pt))
            found = null;

        // 只在高亮目标真的变了时记一笔：真机脚本据此断言"移上去就高亮"（需求 4）
        if (!Equals(found, _hoverTarget))
            Log.Info(found is null
                ? "[hover] 无"
                : $"[hover] 窗口 \"{Describe(found)}\" → {found.Bounds}");

        _hoverTarget = found;
        _hoverWindow = found?.Bounds;
        _hoverTitle = found is null ? null : Describe(found);
    }

    /// <summary>
    /// 悬停标签文案：程序名 → 窗口标题 → 窗口尺寸。
    ///
    /// 用户要的是"在程序名后面加上窗口信息"：只知道标题（"xxx.txt - 记事本"）
    /// 没法判断这是哪个程序、这块有多大的画面，所以把 exe 名和 W×H 一起带上。
    /// </summary>
    private static string Describe(WindowTarget w)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(w.Process)) parts.Add(w.Process);
        if (!string.IsNullOrWhiteSpace(w.Title)) parts.Add(w.Title.Trim());
        parts.Add($"{w.Bounds.Width} × {w.Bounds.Height}");
        return string.Join(" · ", parts);
    }

    private void UpdateMagnifier()
    {
        if (_sampler == null) return;

        // 镜片只在"还没框出选区"或"正在拖动/拉伸选区"时出。
        //
        // 之前是"只要采样点落在画面内就一直画"，于是选区定好以后把鼠标移向胶囊、
        // 次级胶囊或圆角条时，镜片（11×11 网格）会跟着指针压在这些控件上——
        // 正好盖住"粗细"滑杆和数值，看着像是调节条渲染错乱。
        if (!_dragging && Selection.Width > 8)
        {
            HideMagnifier();
            return;
        }

        Native.GetCursorPos(out var c);
        var pt = new Point(c.X, c.Y);

        foreach (var f in _forms)
        {
            if (!_magnifierBuffers.TryGetValue(f, out var buf)) continue;

            var rel = new Point(pt.X - f.PixelOrigin.X, pt.Y - f.PixelOrigin.Y);
            if (rel.X < 0 || rel.Y < 0 || rel.X >= f.PixelSize.Width || rel.Y >= f.PixelSize.Height)
            {
                f.MagnifierPixels = null;
                continue;
            }

            // 采样 11×11 网格
            int half = MagnifierGrid / 2;
            var screenX = pt.X - half;
            var screenY = pt.Y - half;

            using (var g = Graphics.FromImage(buf))
            {
                for (int gy = 0; gy < MagnifierGrid; gy++)
                    for (int gx = 0; gx < MagnifierGrid; gx++)
                    {
                        var col = _sampler.Sample(screenX + gx, screenY + gy);
                        using var b = new SolidBrush(col);
                        g.FillRectangle(b, gx, gy, 1, 1);
                    }
            }

            f.MagnifierPixels = buf;
            f.PixelColor = _sampler.Sample(pt.X, pt.Y);
        }
    }

    /// <summary>收起放大镜（悬停控件、选区已成、鼠标离开画面时都要清）。</summary>
    private void HideMagnifier()
    {
        foreach (var f in _forms) f.MagnifierPixels = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Selection = Rectangle.Empty;
                _finished = true;
                e.Handled = true;
                break;
            case Keys.Enter:
                if (Selection.Width > 0) Finish();
                e.Handled = true;
                break;
        }
    }

    private Rectangle _lastFlyRect;
    private Rectangle _lastPickerRect;
    private Rectangle _lastBarRect;
    private string? _lastHint;
    private Point _lastHintPos;

    /// <summary>手绘控件内部状态变化（选中项、色块、悬停、滑块）请求重绘。</summary>
    private bool _chromeDirty;

    private void OnChromeChanged()
    {
        _chromeDirty = true;
        Redraw();
    }

    /// <summary>提示条的屏幕矩形（与 OverlayForm.DrawHint 的排版一致）。</summary>
    private static Rectangle HintRect(Point screenPos, string text) =>
        new(screenPos.X, screenPos.Y, LabelWidth(text), 26);

    /// <summary>提示条宽度估算（中文按 ~13px/字，留出左右内边距）。</summary>
    private static int LabelWidth(string text) => Math.Max(24, text.Length * 13 + 20);

    /// <summary>
    /// 把标签居中贴在第 <paramref name="index"/> 个胶囊格位上方。
    /// 胶囊贴屏幕顶边时改贴到胶囊下方 —— 不允许标签被裁掉。
    /// </summary>
    private Point CellLabelPos(int index, string text)
    {
        if (_pillScreen.IsEmpty) return Point.Empty;

        float s = _pillScreen.Width / (float)PillToolbar.BaseSize.Width;
        var cell = PillChrome.CellRect(index, s, _pillScreen.Height);
        int w = LabelWidth(text);

        int x = Math.Clamp(
            _pillScreen.Left + cell.Left + cell.Width / 2 - w / 2,
            _virtualBounds.Left + 6,
            Math.Max(_virtualBounds.Left + 6, _virtualBounds.Right - w - 6));

        int y = _pillScreen.Top - 32;
        if (y < _virtualBounds.Top + 6)
        {
            y = _pillScreen.Bottom + 8;
            if (y + 26 > _virtualBounds.Bottom - 4)
                y = Math.Max(_virtualBounds.Top + 6, _pillScreen.Top - 32);
        }

        return new Point(x, y);
    }

    /// <summary>抽一小段消息：让注入的滚轮/重绘真正发生。</summary>
    private void Redraw()
    {
        // 功能条显隐只取决于"有没有选区 / 是否在拖拽"，统一在这里收敛
        SyncBarVisibility();

        bool selectionChanged = Selection != _lastSelection;
        bool cursorChanged = _current != _lastCursor;
        bool snapChanged = _snapHint != _lastSnap;
        bool hoverChanged = _hoverWindow != _lastHover;

        bool dragEnded = _lastDragging && !_dragging;
        bool radiusChanged = Radius != _lastRadius;
        bool radiusBarChanged = RadiusBarScreen != _lastRadiusBarScreen;
        bool shadowChanged = ShadowEnabled != _lastShadowEnabled;

        // 标注变化（含正在绘制）也要算"有变化"，否则会在下面提前 continue
        bool inkChanged = _active != null || _strokes.Count != _lastStrokeCount;

        bool anyChange = selectionChanged || cursorChanged || snapChanged || hoverChanged
                         || radiusChanged || radiusBarChanged || shadowChanged
                         || dragEnded || inkChanged;

        // 同层手绘控件：矩形、可见性、悬停提示变化都要重画。
        // 先保存旧值：下面算脏区时必须用"旧矩形"擦掉残留，不能先用当前值覆盖。
        var oldFlyRect = _lastFlyRect;
        var oldPickerRect = _lastPickerRect;
        var oldBarRect = _lastBarRect;
        var oldHint = _lastHint;
        var oldHintPos = _lastHintPos;

        var flyRect = _flyVisible && _fly != null ? _fly.Bounds : Rectangle.Empty;
        var pickerRect = _popup?.Bounds ?? Rectangle.Empty;
        // 功能条：DirtyBounds 把下拉菜单、收起小柄一起算进来，否则菜单收起后留残影
        var barRect = _bar.Visible ? _bar.DirtyBounds : Rectangle.Empty;
        var hint = _flyVisible ? _fly?.HintText : null;
        var hintPos = _fly?.HintPos ?? Point.Empty;

        // 状态标签（识别中/未识别到文字/已翻译…）贴在**对应的那个格位**上。
        // 优先级高于悬停提示：点完格位鼠标还停在它上面，这时悬停文案会把结果盖掉
        // （实测：点"文字提取"后标签显示的是"文字提取"而不是"未识别到文字"）。
        if (hint == null && _statusText != null && !_pillScreen.IsEmpty)
        {
            int idx = Array.FindIndex(PillToolbar.Cells, c => c.Action == _statusOwner);
            if (idx >= 0)
            {
                hint = _statusText;
                hintPos = CellLabelPos(idx, _statusText);
            }
        }

        // 主胶囊悬停也走同一张位图提示，不再依赖旧的独立 HintTip 窗口。
        if (hint == null && _pillHover >= 0 && !_pillScreen.IsEmpty)
        {
            var text = PillChrome.HintOf(PillChrome.ActionAt(_pillHover));
            if (!string.IsNullOrEmpty(text))
            {
                hint = text;
                hintPos = CellLabelPos(_pillHover, text!);
            }
        }
        bool chromeChanged = flyRect != oldFlyRect || pickerRect != oldPickerRect
                             || barRect != oldBarRect
                             || hint != oldHint || hintPos != oldHintPos;
        anyChange |= chromeChanged || _chromeDirty;

        // 镜片的显示/隐藏常发生在"光标没动"的时候（例如刚拖完选区，镜片按设计收起）。
        // 不把它算作变化、不失效它的矩形，画面上就会留一块放大的残影。
        bool magNow = _forms.Count > 0 && _forms[0].MagnifierVisible
                      && _forms[0].MagnifierPixels != null;
        anyChange |= magNow != _lastMagVisible;

        _lastFlyRect = flyRect;
        _lastPickerRect = pickerRect;
        _lastBarRect = barRect;
        _lastHint = hint;
        _lastHintPos = hintPos;
        _lastRadius = Radius;
        _lastDragging = _dragging;
        _lastStrokeCount = _strokes.Count;

        foreach (var f in _forms)
        {
            f.Selection = Selection;
            f.SelectionScreen = Selection;
            f.Radius = Radius;
            f.RadiusBar = RadiusBarScreen;
            f.PillScreen = _pillScreen;
            f.PillHover = _pillHover;
            f.ShadowEnabled = ShadowEnabled;
            f.Ink = _strokes;
            f.CursorPos = _current;
            f.SnapHint = _snapHint;
            f.Dragging = _dragging;
            f.HoverWindow = _hoverWindow;
            f.HoverTitle = _hoverTitle;
            f.Flyout = _flyVisible ? _fly : null;
            f.Picker = _popup;
            f.Bar = _bar;
            f.HintText = hint;
            f.HintPos = hintPos;

            if (!anyChange) continue;

            var dirty = Rectangle.Empty;

            if (selectionChanged)
            {
                // 边框 2px、控制点 8px 都骑在边框上，向内外各扩 4px 才能擦干净
                if (_lastSelection.Width > 0)
                    dirty = Union(dirty, Rectangle.Inflate(_lastSelection, 4, 4));
                if (Selection.Width > 0)
                    dirty = Union(dirty, Rectangle.Inflate(Selection, 4, 4));
            }

            // 圆角工具条贴在选区外侧，不属于选区矩形。
            // 新旧矩形都要并入脏区：只并入当前矩形时， HideChrome() 之后
            // 旧位置不会被擦掉，会留下"刮刮奖"式重影。
            if (!RadiusBarScreen.IsEmpty) dirty = Union(dirty, RadiusBarScreen);
            if (!_lastRadiusBarScreen.IsEmpty) dirty = Union(dirty, _lastRadiusBarScreen);


            if (radiusChanged && Selection.Width > 8)
                dirty = Union(dirty, Selection);

            // 标注变化（新画一笔/撤销/译文写回）都发生在选区里：
            // 必须把整块选区并入脏区，否则脏区只覆盖胶囊或标签时，
            // 画面看起来"什么都没变"（分层窗口没有系统重绘兜底）。
            if (inkChanged && Selection.Width > 8)
                dirty = Union(dirty, Selection);

            // 胶囊（含悬停高亮）：矩形或悬停格位变化都重画
            if (_pillScreen != _lastPillScreen || _pillHover != _lastPillHover)
            {
                if (!_lastPillScreen.IsEmpty) dirty = Union(dirty, _lastPillScreen);
                if (!_pillScreen.IsEmpty) dirty = Union(dirty, _pillScreen);
            }
            _lastPillScreen = _pillScreen;
            _lastPillHover = _pillHover;

            if (cursorChanged)
            {
                // 首帧 _lastCursor 是哨兵值，减 PixelOrigin 会整数回绕；
                // 此时只需失效当前光标位置。
                if (_lastCursor.X != int.MinValue)
                {
                    dirty = Union(dirty, MagnifierScreenRect(f, _lastCursor));
                    dirty = Union(dirty, SizeBadgeScreenRect(f, _lastCursor));
                }
                dirty = Union(dirty, MagnifierScreenRect(f, _current));
                dirty = Union(dirty, SizeBadgeScreenRect(f, _current));
            }

            if (dragEnded && _lastCursor.X != int.MinValue)
                dirty = Union(dirty, SizeBadgeScreenRect(f, _lastCursor));

            // 镜片刚收起/刚出现：把它的矩形并进脏区（否则残影留在原位）
            if (magNow != _lastMagVisible && _current.X != int.MinValue)
                dirty = Union(dirty, MagnifierScreenRect(f, _current));

            if (selectionChanged && _dragging)
                dirty = Union(dirty, SizeBadgeScreenRect(f, _current));

            if (snapChanged)
            {
                if (_lastSnap.HasValue) dirty = Union(dirty, _lastSnap.Value);
                if (_snapHint.HasValue) dirty = Union(dirty, _snapHint.Value);
            }

            if (hoverChanged)
            {
                if (_lastHover.HasValue) dirty = Union(dirty, _lastHover.Value);
                if (_hoverWindow.HasValue) dirty = Union(dirty, _hoverWindow.Value);
            }

            // 同层控件的旧/新矩形都要失效，否则隐藏时留下残影
            if (!oldFlyRect.IsEmpty) dirty = Union(dirty, oldFlyRect);
            if (!flyRect.IsEmpty) dirty = Union(dirty, flyRect);
            if (!oldPickerRect.IsEmpty) dirty = Union(dirty, oldPickerRect);
            if (!pickerRect.IsEmpty) dirty = Union(dirty, pickerRect);

            // 功能条：阴影向外扩散、状态提示画在条下方 34px，脏区要一起覆盖
            if (!oldBarRect.IsEmpty) dirty = Union(dirty, BarDirty(oldBarRect));
            if (!barRect.IsEmpty) dirty = Union(dirty, BarDirty(barRect));
            if (!string.IsNullOrEmpty(oldHint))
                dirty = Union(dirty, HintRect(oldHintPos, oldHint!));
            if (!string.IsNullOrEmpty(hint))
                dirty = Union(dirty, HintRect(hintPos, hint!));

            // 分层窗口没有 OnPaint 路径：直接把脏区画进同一张位图并提交
            f.Render(dirty.IsEmpty ? null : dirty);
        }

        _lastSelection = Selection;
        _lastCursor = _current;
        _lastSnap = _snapHint;
        _lastHover = _hoverWindow;
        _lastMagVisible = magNow;
        _lastRadiusBarScreen = RadiusBarScreen;
        _lastShadowEnabled = ShadowEnabled;
        _chromeDirty = false;
    }

    private static Rectangle Union(Rectangle a, Rectangle b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return Rectangle.Union(a, b);
    }

    /// <summary>功能条的失效区域：含外扩阴影与条下方的状态提示。</summary>
    private static Rectangle BarDirty(Rectangle bar) =>
        new(bar.Left - 12, bar.Top - 12, bar.Width + 24, bar.Height + 56);

    private static Rectangle LocalRect(OverlayForm f, Rectangle screen)
    {
        if (screen.IsEmpty) return Rectangle.Empty;
        var tl = f.PointToClient(new Point(screen.Left, screen.Top));
        return new Rectangle(tl.X, tl.Y, screen.Width, screen.Height);
    }

    private static Rectangle MagnifierScreenRect(OverlayForm f, Point cursor)
    {
        var local = new Point(cursor.X - f.PixelOrigin.X, cursor.Y - f.PixelOrigin.Y);
        return OverlayForm.GetMagnifierBounds(local, f.PixelSize);
    }

    private static Rectangle SizeBadgeScreenRect(OverlayForm f, Point cursor)
    {
        var local = new Point(cursor.X - f.PixelOrigin.X, cursor.Y - f.PixelOrigin.Y);
        var box = OverlayForm.GetSizeBadgeBounds(local, f.PixelSize);
        return new Rectangle(f.PixelOrigin.X + box.X, f.PixelOrigin.Y + box.Y, box.Width, box.Height);
    }

    public void Dispose()
    {
        HideFlyout();
        DismissPopup();
        _fly = null;

        _bar.Changed -= OnChromeChanged;
        _bar.Visible = false;

        StopInput();

        _statusTimer?.Stop();
        _statusTimer?.Dispose();
        _statusTimer = null;
        _statusText = null;

        foreach (var f in _forms)
        {
            f.Close();
            f.Dispose();
        }
        _forms.Clear();

        foreach (var b in _magnifierBuffers.Values) b.Dispose();
        _magnifierBuffers.Clear();

        _snapshot?.Dispose();
        _snapshot = null;
        _sampler = null;
    }
}

/// <summary>快照的像素采样器（一次 LockBits，纯内存读取）。</summary>
internal sealed class SnapshotSampler
{
    private readonly Bitmap _bmp;
    private readonly byte[] _buf;
    private readonly int _stride;
    private readonly int _w, _h;

    public SnapshotSampler(Bitmap bmp)
    {
        _bmp = bmp;
        _w = bmp.Width;
        _h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, _w, _h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        _stride = data.Stride;
        _buf = new byte[_stride * _h];
        try { System.Runtime.InteropServices.Marshal.Copy(data.Scan0, _buf, 0, _buf.Length); }
        finally { bmp.UnlockBits(data); }
    }

    public Color Sample(int x, int y)
    {
        if (x < 0 || y < 0 || x >= _w || y >= _h) return Color.Black;
        int i = y * _stride + x * 4;
        // BGRA 顺序
        return Color.FromArgb(255, _buf[i + 2], _buf[i + 1], _buf[i]);
    }
}
