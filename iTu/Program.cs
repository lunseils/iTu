using System.Runtime.InteropServices;
using iTu.Core;

namespace iTu;

/// <summary>
/// 程序入口。
///
/// 单实例语义（重要）：
///   截图工具常驻托盘并占用全局热键，如果"旧版本还在跑"，双击新 exe
///   只会被这里唤醒旧进程并静默退出——用户看到的永远是旧代码的行为，
///   于是"改了 N 次都没效果"。
///   因此本类把"谁在跑"变成显式信息：日志记录版本/PID/路径，
///   版本或路径不一致时弹出确认框，允许用户结束旧实例。
/// </summary>
internal static class Program
{
    /// <summary>热键消息窗口的标题（与 HotkeyManager 中 CreateParams.Caption 保持一致）。</summary>
    private const string HotkeySinkTitle = "iTuHotkeySink";

    /// <summary>激活消息窗口的标题（与 AppContext.ActivationWindow 的 Caption 一致）。</summary>
    private const string ActivationTitle = "iTu";

    private const string MutexName = @"Local\iTu.SingleInstance";

    /// <summary>通知已有实例唤起截图的私有消息。</summary>
    private const uint WM_ACTIVATE_APP = 0x8001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? cls, string? win);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // `iTu.exe --settings`：直接打开设置面板（不常驻托盘、不注册热键）。
        // 用途：给"设置"单独做个快捷方式/脚本，也方便真机脚本直接截图设置页。
        if (args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Session($"启动设置面板 {AppInfo.VersionText}");
            Application.Run(new SettingsForm(AppSettings.Load()));
            return;
        }

        Log.Session($"启动 {AppInfo.VersionText} pid={Environment.ProcessId} exe={AppInfo.ExePath}");

        Mutex? mutex = null;
        try
        {
            if (!ResolveInstanceConflict()) return;      // 已有同一构建在跑 → 已唤醒它
            if (!AcquireMutex(out mutex)) { Log.Info("[single] 互斥锁被占用 → 唤醒已有实例后退出"); ActivateExistingInstance(); return; }

            AppInfo.WriteInstanceMarker();
            Log.Info($"[single] 本进程成为主实例，标记文件={AppInfo.InstanceFilePath}");

            RunApp();
        }
        finally
        {
            AppInfo.RemoveInstanceMarker();
            mutex?.Dispose();
        }
    }

    private static void RunApp()
    {
        // 兜底异常处理：托盘常驻程序不应因未捕获异常静默退出
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Info("未处理异常: " + e.Exception);
            ReportFatal(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Info("致命异常: " + e.ExceptionObject);
            ReportFatal(e.ExceptionObject as Exception);
        };

        try
        {
            var ctx = new AppContext();

            // 激活窗口需在消息泵就绪后创建：挂到首次 Idle 回调上
            bool started = false;
            Application.Idle += (_, _) =>
            {
                if (started) return;
                started = true;
                try
                {
                    ctx.Start();
                    Log.Info("激活窗口已创建");
                }
                catch (Exception ex)
                {
                    Log.Info("创建激活窗口失败: " + ex);
                }
            };

            Application.Run(ctx);
            Log.Info("消息循环结束（正常退出）");
        }
        catch (Exception ex)
        {
            Log.Info("主循环异常: " + ex);
            ReportFatal(ex);
        }
    }

    // ---------------- 单实例 ----------------

    /// <summary>
    /// 处理"已有实例在运行"的情形。
    /// 返回 true 表示可以继续（本进程将作为主实例启动）。
    /// </summary>
    private static bool ResolveInstanceConflict()
    {
        var other = FindOtherInstance();
        if (other == null) return true;

        Log.Info("[single] 检测到已有 iTu 在运行：" + other);

        if (other.IsSameBuild(AppInfo.Version, AppInfo.ExePath))
        {
            // 正常双击：唤醒已有实例去截图，本进程退出
            Log.Info("[single] 与当前构建一致 → 唤醒已有实例并退出（正常单实例行为）");
            ActivateExistingInstance();
            return false;
        }

        // 版本 / 路径不一致：极可能是旧版本残留，必须让用户明确知道
        Log.Info("[single] 运行中的实例不是当前构建！热键被它占着，实际运行的是旧代码");
        var answer = MessageBox.Show(
            "检测到另一个 iTu 正在运行，而且**不是**当前这个版本：\n\n" +
            $"　运行中：版本 {other.Version}　PID {other.Pid}\n" +
            $"　　　　　{other.ExePath}\n\n" +
            $"　本次启动：版本 {AppInfo.VersionText}\n" +
            $"　　　　　{AppInfo.ExePath}\n\n" +
            "全局热键只会发给先启动的那个实例，所以你现在测到的仍是旧版本的行为。\n" +
            "是否结束旧实例并改用当前版本？",
            "iTu 版本冲突",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);

        if (answer != DialogResult.Yes)
        {
            Log.Info("[single] 用户选择保留运行中的旧实例 → 本进程退出");
            return false;
        }

        if (!AppInfo.TryTerminate(other, out var error))
        {
            Log.Info("[single] 结束旧实例失败：" + error);
            MessageBox.Show("无法结束旧实例：" + error + "\n请在托盘图标上右键退出后重试。",
                "iTu", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        Log.Info($"[single] 已结束旧实例 pid={other.Pid}（版本 {other.Version}）");
        // 旧实例已被强杀，其标记已失效；后续 WriteInstanceMarker 会写入本进程的新标记覆盖它。
        return true;
    }

    /// <summary>查找正在运行的其它实例；没有则返回 null。</summary>
    private static InstanceInfo? FindOtherInstance()
    {
        // 1) 优先读实例标记：能拿到版本、路径、启动时间
        var marker = AppInfo.TryReadInstanceMarker();
        if (marker != null &&
            marker.Pid != Environment.ProcessId &&
            AppInfo.IsProcessAlive(marker.Pid))
        {
            return marker;
        }

        // 2) 老版本可能没有标记文件：用窗口标题兜底，并从窗口反查 PID。
        // 必须同时读取该 PID 的真实路径与启动时间：如果只给 PID 却把路径写成占位符，
        // TryTerminate 会因无法校验而走 fail-open 分支直接 Kill——PID 被复用时会误杀无关进程。
        var hwnd = FindWindow(null, HotkeySinkTitle);
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0 && pid != (uint)Environment.ProcessId)
            {
                string? path = null;
                DateTime started = DateTime.MinValue;
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    started = p.StartTime;
                    path = p.MainModule?.FileName;
                }
                catch { /* 读不到身份信息 → 下方拒绝返回，避免无校验地结束进程 */ }

                if (string.IsNullOrEmpty(path) || !System.IO.Path.IsPathFullyQualified(path))
                {
                    Log.Info($"[single] 窗口兜底命中 pid={pid} 但无法读取其可执行路径，忽略以免误杀");
                    return null;
                }
                return new InstanceInfo((int)pid, "未知（该实例未记录版本）", path, started);
            }
        }

        return null;
    }

    /// <summary>抢占主实例互斥锁。</summary>
    private static bool AcquireMutex(out Mutex? mutex)
    {
        mutex = null;
        try
        {
            var m = new Mutex(true, MutexName, out var created);
            if (created)
            {
                mutex = m;
                return true;
            }

            // 对象已存在：要么真有人持有，要么上一实例刚崩溃（abandoned）
            try
            {
                if (m.WaitOne(0, false))
                {
                    mutex = m;
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
                mutex = m;
                return true;
            }

            m.Dispose();
            return false;
        }
        catch (Exception ex)
        {
            // 互斥锁不可用时宁可让它跑起来，也不要让程序根本起不来
            Log.Info("[single] 互斥锁异常（忽略并继续启动）：" + ex.Message);
            return true;
        }
    }

    /// <summary>通过自定义消息通知已运行的实例唤起截图。</summary>
    private static void ActivateExistingInstance()
    {
        // WM_ACTIVATE_APP 只有 AppContext.ActivationWindow（标题 "iTu"）会处理。
        // 旧实现发给热键窗口 "iTuHotkeySink"，消息落到 DefWindowProc 被丢弃，
        // 表现为"再次双击 exe 没有任何反应"。
        var hwnd = FindWindow(null, ActivationTitle);
        if (hwnd == IntPtr.Zero)
        {
            // 旧版本实例可能还没有激活窗口：退回热键窗口保证至少能被发现。
            hwnd = FindWindow(null, HotkeySinkTitle);
        }
        if (hwnd == IntPtr.Zero)
        {
            Log.Info("[single] 未找到已有实例的激活窗口，无法唤醒");
            return;
        }
        PostMessage(hwnd, WM_ACTIVATE_APP, IntPtr.Zero, IntPtr.Zero);
    }

    private static void ReportFatal(Exception? ex)
    {
        Log.Info("严重错误: " + ex);
        try
        {
            // 仅判断交互式会话：WinExe 没有控制台，Console.IsOutputRedirected
            // 恒为 true，用它做条件会导致致命错误永远不弹窗（旧实现的缺陷）。
            if (!Environment.UserInteractive) return;
            MessageBox.Show("发生未处理的异常：\n" + ex?.Message, "iTu",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // 兜底：连弹窗都失败时静默处理
        }
    }
}
