using System.Runtime.InteropServices;

namespace iTu.Core;

/// <summary>显示器信息（物理像素）。</summary>
public sealed class MonitorInfo
{
    public Rectangle Bounds { get; init; }
    public Rectangle WorkArea { get; init; }
    public bool IsPrimary { get; init; }
    public string DeviceName { get; init; } = "";

    public override string ToString() => $"{DeviceName} {Bounds}";
}

/// <summary>
/// 显示器枚举。用 EnumDisplayMonitors + GetMonitorInfo 获取物理像素矩形，
/// 避免 Screen.AllScreens 在混合 DPI 下返回逻辑坐标导致的错位。
/// </summary>
public static class DisplayInfo
{
    // 必须与下面 DllImport 的 CharSet.Unicode 一致：
    // ByValTStr 在 Unicode 下占 32*2 字节，Ansi 下只占 32 字节。
    // 两者不一致会让 GetMonitorInfoW 写入超出结构体，导致设备名乱码甚至内存破坏。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT rcMonitor;
        public Native.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprc, MonitorEnumProc lpfn, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        MonitorEnumProc cb = (hMon, _, _, _) =>
        {
            var info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty
            };
            if (GetMonitorInfo(hMon, ref info))
            {
                list.Add(new MonitorInfo
                {
                    Bounds = RectOf(info.rcMonitor),
                    WorkArea = RectOf(info.rcWork),
                    IsPrimary = (info.dwFlags & 1) != 0,
                    DeviceName = info.szDevice
                });
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

        if (list.Count == 0)
        {
            // 兜底：至少返回主屏
            var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            list.Add(new MonitorInfo { Bounds = b, WorkArea = b, IsPrimary = true, DeviceName = @"\\.\DISPLAY1" });
        }
        return list;
    }

    /// <summary>虚拟桌面总范围（物理像素，可含负坐标）。</summary>
    public static Rectangle GetVirtualBounds(List<MonitorInfo> monitors)
    {
        if (monitors.Count == 0) return new Rectangle(0, 0, 1920, 1080);
        int left = monitors.Min(m => m.Bounds.Left);
        int top = monitors.Min(m => m.Bounds.Top);
        int right = monitors.Max(m => m.Bounds.Right);
        int bottom = monitors.Max(m => m.Bounds.Bottom);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static Rectangle RectOf(Native.RECT r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
}
