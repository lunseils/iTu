using System.Runtime.InteropServices;
using System.Text;

namespace QaCheck;

/// <summary>
/// QA 侧独立使用的 Win32 封装。
/// 刻意与产品代码 Core/Native.cs 分开，避免"用被测代码验证被测代码"。
/// </summary>
internal static class W
{
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_HOTKEY = 0x0312;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    public const byte VK_CONTROL = 0x11;
    public const byte VK_MENU = 0x12;
    public const byte VK_ESCAPE = 0x1B;
    public const byte VK_RETURN = 0x0D;
    public const byte VK_SHIFT = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static void SetWindowPosPublic(IntPtr h, RECT r) =>
        SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, 0x0004 /*SWP_NOZORDER*/);

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // ---- 高层封装 ----

    public static int ScreenW => GetSystemMetrics(0);
    public static int ScreenH => GetSystemMetrics(1);

    /// <summary>
    /// 以绝对坐标移动鼠标。
    /// 关键：SetCursorPos 不会进入 WH_MOUSE_LL 钩子输入流，用它会得到假阳性，
    /// 因此这里一律走 MOUSEEVENTF_MOVE|ABSOLUTE。
    /// </summary>
    public static void MoveTo(int x, int y)
    {
        int sw = Math.Max(1, ScreenW - 1), sh = Math.Max(1, ScreenH - 1);
        int nx = (int)((long)Math.Clamp(x, 0, sw) * 65535 / sw);
        int ny = (int)((long)Math.Clamp(y, 0, sh) * 65535 / sh);
        mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, nx, ny, 0, IntPtr.Zero);
    }

    public static void LeftDown() => mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
    public static void LeftUp() => mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);

    public static void KeyDown(byte vk) => keybd_event(vk, 0, 0, IntPtr.Zero);
    public static void KeyUp(byte vk) => keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);

    public static void Press(byte vk) { KeyDown(vk); Thread.Sleep(30); KeyUp(vk); }

    public static void TapCtrl(byte vk)
    {
        KeyDown(VK_CONTROL); Thread.Sleep(20);
        KeyDown(vk); Thread.Sleep(30);
        KeyUp(vk); Thread.Sleep(20);
        KeyUp(VK_CONTROL);
    }

    public static string WindowText(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string WindowClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static List<(IntPtr Hwnd, string Title, string Class, RECT Rect)> VisibleWindows()
    {
        var list = new List<(IntPtr, string, string, RECT)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowRect(h, out var r);
            if (r.Width <= 0 || r.Height <= 0) return true;
            list.Add((h, WindowText(h), WindowClass(h), r));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IEnumerable<IntPtr> TopLevelWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
