using System.Drawing.Imaging;
using iTu.Core;
using iTu.Ui;

namespace iTu;

/// <summary>
/// 应用主体：托盘常驻 + 全局热键 + 截图流程编排。
/// </summary>
public sealed class AppContext : ApplicationContext
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HotkeyManager _hotkeys = new();
    private readonly NotifyIcon _tray;


    public AppContext()
    {
        BuildTray();

        _tray = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "iTu 截图工具",
            Visible = false
        };
        _tray.DoubleClick += (_, _) => Safe(() => Capture(CaptureMode.Region));

        // 右键弹自绘菜单（系统托盘图标没有"菜单打开"事件，自己判右键）
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            Safe(() => Ui.CapsuleMenu.Popup(Cursor.Position, _trayMenuItems));
        };
        RegisterHotkeys();
        ApplyStartup();

        _tray.Visible = true;
        UpdateTrayText();

        Log.Info($"[app] 已启动 {AppInfo.VersionText} pid={Environment.ProcessId} " +
                 $"热键={_settings.CaptureHotkey}/{_settings.FullscreenHotkey}/{_settings.RecordHotkey} 日志={Log.FilePath}");

        // 托盘程序启动后没有任何界面，用一条气泡提示就够。
        // 旧版还会额外弹一个 340×232 的"启动提示面板"（置顶 6 秒），
        // 结果用户按快捷键截图时它正好被拍进画面，成了"右上角多余的窗口"。

        _tray.ShowBalloonTip(3000, "iTu", "截图工具已启动，按 " + _settings.CaptureHotkey + " 截图", ToolTipIcon.Info);
    }

    /// <summary>
    /// 隐藏的命名窗口：用于接收"再次启动"的激活消息。
    ///
    /// 注意：必须在消息泵启动之后创建（CreateHandle 需要有效的 WinForms 上下文），
    /// 因此延迟到 <see cref="Start"/> 中初始化。
    /// </summary>
    private sealed class ActivationWindow : NativeWindow
    {
        public const uint WM_ACTIVATE_APP = 0x8001;
        public event Action? Activated;

        public ActivationWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "iTu",
                X = 0, Y = 0, Width = 0, Height = 0,
                Style = 0,
                ExStyle = 0x00000080 // WS_EX_TOOLWINDOW
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ACTIVATE_APP)
            {
                Activated?.Invoke();
                return;
            }
            base.WndProc(ref m);
        }
    }

    private ActivationWindow? _activation;

    /// <summary>消息泵就绪后调用：创建激活窗口。</summary>
    public void Start()
    {
        _activation = new ActivationWindow();
        _activation.Activated += OnActivated;
    }

    private void OnActivated()
    {
        // 已有实例被再次启动 → 直接唤起区域截图
        Safe(() => Capture(CaptureMode.Region));
    }

    /// <summary>
    /// 托盘右键菜单：用自绘的 <see cref="Ui.CapsuleMenu"/> 取代系统 ContextMenuStrip。
    ///
    /// 截图入口只保留「快捷键 / 双击托盘」两条：菜单里再列一遍
    /// 区域/窗口/全屏/录屏/文字提取只会让菜单变长、还容易选错模式
    /// （那几项原来各自带一套不同的结束语义）。这里只留真正低频但必要的管理项。
    /// </summary>
    private void BuildTray()
    {
        _trayMenuItems = new[]
        {
            new Ui.CapsuleMenu.MenuItem("设置…",      Ui.IconKind.Gear,      () => Safe(() => OpenSettings())),
            new Ui.CapsuleMenu.MenuItem("关于 / 诊断…", Ui.IconKind.Info, () => Safe(ShowDiagnostics)),
            new Ui.CapsuleMenu.MenuItem("打开日志文件", Ui.IconKind.Folder,   () => Safe(OpenLogFile)),
            Ui.CapsuleMenu.MenuItem.Separator,
            new Ui.CapsuleMenu.MenuItem("退出",       Ui.IconKind.Close,     ExitApp),
        };
    }

    private Ui.CapsuleMenu.MenuItem[] _trayMenuItems = Array.Empty<Ui.CapsuleMenu.MenuItem>();

    /// <summary>与图 2 一致的图标绿。</summary>
    private static readonly Color TrayGreen = Color.FromArgb(122, 187, 62);

    private static Icon CreateTrayIcon()
    {
        // 优先用程序自身的主图标，保证托盘/任务栏/窗口图标完全一致
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                using var main = Icon.ExtractAssociatedIcon(exe);
                if (main != null) return (Icon)main.Clone();
            }
        }
        catch (Exception ex)
        {
            Log.Info("[tray] 读取主图标失败，回退到内置绘制: " + ex.Message);
        }

        // 透明背景 + 绿色剪刀（与程序图标同一套绘制，保证视觉一致）
        using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Ui.Icons.Draw(g, Ui.IconKind.ScissorSolid, new RectangleF(1.5f, 1.5f, 29, 29), TrayGreen);
        }

        // Icon.FromHandle 不接管 HICON 所有权：必须 Clone 出独立图标并销毁原句柄，
        // 否则每创建一次托盘图标就泄漏一个 GDI 图标句柄（旧实现的缺陷）。
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    /// <summary>热键注册状态（用于托盘提示与「关于」对话框，便于用户自证）。</summary>
    private string _hotkeyState = "未注册";

    private void RegisterHotkeys()
    {
        _hotkeys.Register(_settings.CaptureHotkey, () => Safe(() => Capture(CaptureMode.Region)), out var err1);
        _hotkeys.Register(_settings.FullscreenHotkey, () => Safe(CaptureFullScreen), out var err3);
        _hotkeys.Register(_settings.RecordHotkey, () => Safe(() => StartRecording()), out var err2);

        var errors = ((err1 ?? "") + (err2 ?? "") + (err3 ?? "")).Trim();
        if (errors.Length == 0)
        {
            _hotkeyState = "已注册";
            Log.Info($"[hotkey] 已注册 自由截图={_settings.CaptureHotkey} " +
                     $"全屏截图={_settings.FullscreenHotkey} 录制={_settings.RecordHotkey}");
            return;
        }

        // 热键注册失败的典型原因就是"另一个实例（往往是旧版本）已经占用了它"。
        // 这种情况必须 loudly 告诉用户，否则他会以为"改了没用"。
        _hotkeyState = "注册失败：" + errors;
        Log.Info("[hotkey] 注册失败：" + errors);
        UpdateTrayText();

        try
        {
            var toast = new Toast("快捷键注册失败：" + errors, Theme.Warning);
            toast.Show();
        }
        catch
        {
            // 忽略提示失败
        }

        try
        {
            _tray.ShowBalloonTip(6000, "iTu 快捷键未生效",
                "该组合可能已被旧版 iTu 或其他程序占用。\n" +
                "请右键托盘图标 →「关于 / 诊断」查看当前版本与日志路径，" +
                "或在设置里换一个组合。",
                ToolTipIcon.Warning);
        }
        catch
        {
            // 托盘未就绪时忽略
        }
    }

    /// <summary>托盘提示文本：带上版本号与启动时间，用户一眼能确认在跑的是哪一份构建。</summary>
    private void UpdateTrayText()
    {
        try
        {
            var text = $"iTu {AppInfo.VersionText}";
            if (_recordPanel is { IsDisposed: false }) text += " ● 录制中";
            if (_hotkeyState != "已注册") text += " ⚠热键" + (_hotkeyState.StartsWith("注册失败") ? "未生效" : "未注册");
            _tray.Text = text.Length > 63 ? text[..63] : text;   // NotifyIcon 文本上限
        }
        catch
        {
            // 文本非法时忽略
        }
    }

    /// <summary>「关于 / 诊断」：把版本、进程、路径、日志与热键状态摊开给用户看。</summary>
    private void ShowDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"iTu {AppInfo.VersionText}");
        sb.AppendLine();
        sb.AppendLine($"进程 PID：{Environment.ProcessId}");
        sb.AppendLine($"启动时间：{AppInfo.StartTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"程序路径：{AppInfo.ExePath}");
        sb.AppendLine($"自由截图热键：{_settings.CaptureHotkey}（{_hotkeyState}）");
        sb.AppendLine($"全屏截图热键：{_settings.FullscreenHotkey}");
        sb.AppendLine($"录制热键：{_settings.RecordHotkey}");
        sb.AppendLine($"日志文件：{Log.FilePath}");

        var marker = AppInfo.TryReadInstanceMarker();
        if (marker != null)
        {
            sb.AppendLine();
            sb.AppendLine($"实例标记：版本 {marker.Version} / PID {marker.Pid}");
            sb.AppendLine($"　　　　　{marker.ExePath}");
        }

        MessageBox.Show(sb.ToString(), "iTu 关于 / 诊断",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>用记事本打开日志（没有 .log 关联时也能看）。</summary>
    private void OpenLogFile()
    {
        try
        {
            if (!File.Exists(Log.FilePath))
            {
                Toast.Show(null, "日志文件还不存在：先复现一次问题再看", Theme.Warning);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + Log.FilePath + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Toast.Show(null, "打开日志失败：" + ex.Message, Theme.Danger);
        }
    }

    private void ApplyStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            const string ValueName = "iTu";
            const string LegacyValueName = "iTu";   // 旧产品名，升级时清理

            if (key.GetValue(LegacyValueName) != null) key.DeleteValue(LegacyValueName);

            if (_settings.RunOnStartup)
            {
                var exePath = Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
            }
        }
        catch
        {
            // 注册表不可写时忽略
        }
    }

    /// <summary>
    /// 忙碌标志：截图/录制/文字提取/设置任一流程进行中都不允许重入。
    ///
    /// 旧实现只保护了"区域截图"，其余入口（录制、OCR）都能被热键或托盘
    /// 二次触发，导致两个 OverlaySession 同时存在（各自装钩子、互相覆盖选区）。
    /// </summary>
    private bool _busy;

    private void Safe(Action action)
    {
        if (_busy)
        {
            Log.Info("[app] 上一次操作尚未结束，忽略本次触发");
            return;
        }

        _busy = true;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Info("[app] 操作失败: " + ex);
            MessageBox.Show("操作失败：" + ex.Message, "iTu",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void Capture(CaptureMode mode)
    {
        // 区域/窗口/全屏统一走截图界面：选区、圆角、标注、颜色、钉、分享、保存
        // 全部在遮罩层内完成，不再有第二个编辑器窗口。
        CaptureRegion(mode);
    }

    private void CaptureRegion(CaptureMode mode)
    {
        // 抓屏前先收掉自己的浮动提示：它们是 TopMost 的独立窗口，
        // 留在屏幕上就会被拍进画面（就是"截图里多出来一个窗口"的根源之一）。
        Ui.Toast.CloseAll();
        var settleUntil = Environment.TickCount64 + 80;
        while (Environment.TickCount64 < settleUntil)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        using var session = new OverlaySession(_settings, mode);
        bool ok = session.Run(mode);

        // 顶部功能条上点了「录屏」：本次截图会话只是"让出舞台"，
        // 接着走录制流程（录制自己会再框一次区域）。
        if (session.RecordRequested)
        {
            session.Result?.Dispose();
            StartRecording();
            return;
        }

        if (!ok || session.Result is not { } img) return;

        // 顶部功能条的「全屏截图」也是走这条路：选区 = 整块虚拟桌面。
        // 顶部功能条的"保存"：直接写文件
        if (session.SaveRequested)
        {
            using (img)
            {
                if (Core.ResultWriter.TrySave(img, _settings, out var path))
                    _tray.ShowBalloonTip(1600, "iTu", "已保存 " + System.IO.Path.GetFileName(path), ToolTipIcon.None);
            }
            return;
        }

        // 钉在桌面：直接钉出
        if (session.PinRequested)
        {
            var pin = new Ui.PinWindow(img);   // PinWindow 接管位图所有权
            pin.Show();
            pin.BringToFront();
            return;
        }

        // 分享：弹出分享菜单
        if (session.ShareRequested)
        {
            ShowShareForImage(img, session.Selection);
            return;
        }

        // 识图 / 翻译：识别与翻译都在截图界面里完成，并**直接写回选区画面**，
        // 不会再出图后弹一个文字结果窗口（那个窗口以前还落在屏幕左上角）。
        // 这里只处理"用户随后按了保存/钉/分享"的常规收尾。

        // 兜底：未明确选择保存/钉/分享时，默认保存
        using (img)
        {
            if (!Core.ResultWriter.TrySave(img, _settings, out _))
                Toast.Show(null, "保存失败，请检查保存目录与磁盘空间", Theme.Danger);
        }
    }

    /// <summary>
    /// 全屏截图热键：不弹截图界面（全屏没有可框选的东西），直接抓整块虚拟桌面。
    ///
    /// 浮层路径里的"全屏截图"（顶部功能条菜单）仍然会经过遮罩层，
    /// 那样可以标注/加圆角；热键这条路只求快，抓到就存盘。
    /// </summary>
    private void CaptureFullScreen()
    {
        Ui.Toast.CloseAll();
        var settleUntil = Environment.TickCount64 + 80;
        while (Environment.TickCount64 < settleUntil)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        var bounds = DisplayInfo.GetVirtualBounds(DisplayInfo.GetMonitors());
        var snapshot = ScreenCapture.CaptureDesktop(bounds);
        using (snapshot.Image)
        {
            if (Core.ResultWriter.TrySave(snapshot.Image, _settings, out var path))
            {
                Log.Info($"[fullscreen] 已保存 {path}");
                _tray.ShowBalloonTip(1600, "iTu", "已保存 " + System.IO.Path.GetFileName(path),
                    ToolTipIcon.None);
            }
            else
            {
                Toast.Show(null, "保存失败，请检查保存目录与磁盘空间", Theme.Danger);
            }
        }
    }

    /// <summary>分享刚截取的图：弹分享菜单。</summary>
    private void ShowShareForImage(Bitmap image, Rectangle anchor)
    {
        // 用一个临时宿主窗口承接菜单（IWin32Window）
        var host = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Size = new Size(1, 1),
            Opacity = 0,
            Location = new Point(anchor.Left, anchor.Top)
        };
        host.Show();

        var origin = new Point(anchor.Left, anchor.Bottom + 6);
        var menu = Ui.ShareService.ShowMenu(host, origin,
            () => (Bitmap)image.Clone(),
            _ => { },
            () => { if (!Core.ResultWriter.TrySave(image, _settings, out var _)) Toast.Show(host, "保存失败，请检查目标目录", Theme.Danger); });

        // 菜单关闭后回收宿主窗口与位图；否则每次分享都会永久留下一个隐藏 Form。
        menu.FormClosed += (_, _) =>
        {
            image.Dispose();
            host.Close();
            host.Dispose();
        };
    }

    // Safe() 的 _busy 只覆盖到异步任务"启动"为止；录制/OCR 的实际工作在后台继续，
    // 需要各自的长流程标志，防止热键二次触发开出第二个录制器/OCR 会话。
    private int _recordingRunning;

    /// <summary>当前在录的录制面板（null 表示没有录制）。热键/托盘再次触发 = 停止它。</summary>
    private RecordPanel? _recordPanel;

    private void StartRecording()
    {
        // 录制中再次触发（录制热键 / 托盘「停止录制」）＝ 停止并保存，
        // 而不是像旧实现那样只提示"已有录制任务进行中"——那样用户只能去点面板。
        if (_recordPanel is { IsDisposed: false } running)
        {
            running.StopAndSave();
            return;
        }

        if (Interlocked.CompareExchange(ref _recordingRunning, 1, 0) != 0)
        {
            Toast.Show(null, "已有录制任务进行中", Theme.Warning);
            return;
        }

        bool handedOff = false;
        try
        {
            using var session = new OverlaySession(_settings, CaptureMode.Region);
            if (!session.Run(CaptureMode.Region)) return;

            var region = session.Selection;
            var recorder = new ScreenRecorder(_settings, region);
            var panel = new RecordPanel(recorder);

            _recordPanel = panel;
            panel.FormClosed += (_, _) =>
            {
                _recordPanel = null;

                // 面板在关闭前已经把 Stop/Cancel 跑完（见 RecordPanel.OnFormClosing），
                // 这里只负责回收：Dispose 对已停止的录制器是空操作。
                recorder.Dispose();
                Interlocked.Exchange(ref _recordingRunning, 0);
                UpdateTrayText();
            };

            handedOff = true;

            try
            {
                panel.Show();
                recorder.Start();
            }
            catch (Exception ex)
            {
                Log.Info("[record] 启动失败: " + ex);
                _recordPanel = null;
                panel.Dispose();
                recorder.Dispose();
                handedOff = false;
                throw;
            }
        }
        finally
        {
            // 取消选区 / 启动失败时没有 FormClosed 事件，必须在这里复位标志
            if (!handedOff) Interlocked.Exchange(ref _recordingRunning, 0);
        }
    }

    private void OpenSettings()
    {
        var form = new SettingsForm(_settings);
        form.ShowDialog();
        _hotkeys.UnregisterAll();
        RegisterHotkeys();
        ApplyStartup();
    }

    private void ExitApp()
    {
        // 录制是后台长流程，_busy 覆盖不到它。若正在录制，先停止并保存 ——
        // StopAndSave 是异步落盘的，不能立刻 ExitThread（否则保存会被进程退出打断），
        // 所以这里先触发停止、本次不退出，待面板关闭后再退出即可。
        if (_recordPanel is { IsDisposed: false } recording)
        {
            recording.StopAndSave();
            return;
        }

        if (_busy)
        {
            var r = MessageBox.Show("当前有任务进行中，仍要退出吗？", "iTu",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
        }

        _tray.Visible = false;
        _tray.Icon?.Dispose();          // 托盘图标是 HICON，必须显式释放
        _tray.Dispose();
        _hotkeys.Dispose();
        if (_activation != null)
        {
            _activation.Activated -= OnActivated;
            _activation.DestroyHandle();
            _activation = null;
        }
        Log.Info("[app] 用户退出");
        ExitThread();
    }
}
