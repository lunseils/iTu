using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace iTu.Core;

public enum CaptureMode
{
    /// <summary>区域截图：拖拽框选。</summary>
    Region,

    /// <summary>窗口截图：悬停高亮窗口。</summary>
    Window,

    /// <summary>全屏截图：整块虚拟桌面。</summary>
    FullScreen
}

/// <summary>一个可被"窗口截图"选中的顶层窗口。</summary>
/// <param name="Handle">窗口句柄。</param>
/// <param name="Bounds">DWM 真实边框（屏幕物理像素）。</param>
/// <param name="Title">窗口标题（悬停标签用）。</param>
/// <param name="Process">所属程序名（如 notepad、chrome），拿不到时是空串。</param>
public sealed record WindowTarget(IntPtr Handle, Rectangle Bounds, string Title, string Process = "");

/// <summary>一次屏幕采集的结果：整块虚拟桌面 + 可见窗口列表。</summary>
public sealed class ScreenSnapshot
{
    /// <summary>虚拟桌面总区域（多显示器合并，可能含负坐标）。</summary>
    public Rectangle VirtualBounds { get; init; }

    /// <summary>虚拟桌面截图。</summary>
    public Bitmap Image { get; init; } = null!;

    /// <summary>
    /// 可见窗口列表：**按 z 序从上到下**（EnumWindows 的顺序），
    /// 悬停高亮取第一个命中的即可，不用自己排序。
    /// 已剔除不可见/最小化窗口、工具窗口、系统外壳窗口与本进程自己的窗口。
    /// </summary>
    public List<WindowTarget> Windows { get; init; } = new();
}

public static class ScreenCapture
{
    /// <summary>
    /// 采集整块虚拟桌面画面，并枚举可用于"窗口截图"的窗口。
    /// </summary>
    /// <param name="virtualBounds">
    /// 虚拟桌面物理像素矩形。必须与后续坐标换算使用同一个矩形，
    /// 否则快照位图尺寸与逻辑坐标系错位（DPI 缩放或多屏时会偏）。
    /// </param>
    public static ScreenSnapshot CaptureDesktop(Rectangle virtualBounds)
    {
        var bounds = virtualBounds;
        var bmp = Grab(bounds);

        return new ScreenSnapshot
        {
            VirtualBounds = bounds,
            Image = bmp,
            Windows = EnumerateTargets(bounds)
        };
    }

    /// <summary>桌面/任务栏等系统外壳窗口的类名：它们不该被当成"可截取的窗口"。</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "TaskListThumbnailWnd", "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow", "ForegroundStaging",
        "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "SysShadow", "IME"
    };

    /// <summary>
    /// 枚举可选窗口（z 序从上到下）。
    ///
    /// 过滤规则每一条都有具体来由：
    ///   · 本进程窗口：截图浮层本身、提示条都属于这里，绝不能成为"悬停目标"；
    ///   · WS_EX_TOOLWINDOW：输入法候选、托盘气泡、我们的 Toast 都是工具窗口；
    ///   · 系统外壳类名：Progman/WorkerW 铺满整屏，一命中就会把整屏高亮成"一个窗口"；
    ///   · 无标题 &lt; 80×60：多半是隐藏的宿主窗口，选中没有意义。
    /// </summary>
    public static List<WindowTarget> EnumerateTargets(Rectangle bounds)
    {
        var list = new List<WindowTarget>();
        uint self = (uint)Environment.ProcessId;

        // 同一进程的多个窗口只查一次（Process 查询比 EnumWindows 回调贵得多）
        var procNames = new Dictionary<uint, string>();

        Native.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;

            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;

            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;

            if (ShellClasses.Contains(ClassNameOf(hwnd))) return true;

            var title = GetWindowTitle(hwnd).Trim();
            if (title.Length == 0) return true;

            if (!TryGetWindowRect(hwnd, out var rect)) return true;
            if (rect.Width < 80 || rect.Height < 60) return true;
            if (!bounds.IntersectsWith(rect)) return true;

            if (!procNames.TryGetValue(pid, out var proc))
            {
                proc = ProcessNameOf(pid);
                procNames[pid] = proc;
            }

            list.Add(new WindowTarget(hwnd, rect, title, proc));
            return true;
        };

        Native.EnumWindows(callback, IntPtr.Zero);
        return list;
    }

    /// <summary>进程显示名（exe 名去扩展名）；取不到时退回空串，绝不抛异常。</summary>
    public static string ProcessNameOf(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            return string.IsNullOrWhiteSpace(name) ? "" : name.Trim();
        }
        catch
        {
            // 受保护进程 / 已退出的进程：查不到就不显示，不影响其余信息
            return "";
        }
    }

    /// <summary>取窗口类名（失败返回空串）。</summary>
    public static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(128);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 抓取指定屏幕区域。
    ///
    /// 优先用 GDI BitBlt（SRCCOPY | CAPTUREBLT）直接从屏幕 DC 拷进内存位图：
    /// 比 GDI+ 的 CopyFromScreen 少一层建 DC / 格式转换的开销，
    /// 2560×1440 全屏实测由 ~33ms 降到 ~10ms 量级。
    /// 失败时自动退回 CopyFromScreen，保证功能不受影响。
    /// </summary>
    private static Bitmap Grab(Rectangle bounds)
    {
        int w = Math.Max(1, bounds.Width);
        int h = Math.Max(1, bounds.Height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        Graphics? g = null;
        bool ok = false;

        try
        {
            hdcScreen = Native.GetDC(IntPtr.Zero);
            if (hdcScreen != IntPtr.Zero)
            {
                g = Graphics.FromImage(bmp);
                hdcMem = g.GetHdc();
                ok = Native.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, bounds.Left, bounds.Top,
                    Native.SRCCOPY | Native.CAPTUREBLT);
            }
        }
        catch
        {
            ok = false;
        }
        finally
        {
            if (hdcMem != IntPtr.Zero) g?.ReleaseHdc(hdcMem);
            g?.Dispose();
            if (hdcScreen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, hdcScreen);
        }

        if (!ok)
        {
            using var fallback = Graphics.FromImage(bmp);
            fallback.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        // BitBlt 只保证 RGB，屏幕 DC 的 alpha 字节是 0，这里统一补成不透明，
        // 否则后续 GDI+ 合成会把它当成全透明图。
        ForceOpaque(bmp);
        return bmp;
    }

    /// <summary>把 32bpp 位图的 alpha 通道全部置为不透明。</summary>
    private static void ForceOpaque(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[bmp.Width];
            for (int y = 0; y < bmp.Height; y++)
            {
                var p = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(p, row, 0, row.Length);
                for (int x = 0; x < row.Length; x++) row[x] |= unchecked((int)0xFF000000);
                Marshal.Copy(row, 0, p, row.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>按 DWM 真实边框取窗口矩形；失败则退回 GetWindowRect。</summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        var ok = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var dwm, Marshal.SizeOf<Native.RECT>()) == 0;
        if (!ok)
        {
            if (!Native.GetWindowRect(hwnd, out var wr)) return false;
            dwm = wr;
        }
        if (dwm.Width <= 0 || dwm.Height <= 0) return false;
        rect = Rectangle.FromLTRB(dwm.Left, dwm.Top, dwm.Right, dwm.Bottom);
        return true;
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var len = Native.GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new System.Text.StringBuilder(len + 2);
        Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

}