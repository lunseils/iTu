using System.Runtime.InteropServices;

namespace iTu.Core;

/// <summary>
/// 全部 Win32 互操作声明集中于此，便于审阅与维护。
/// </summary>
internal static class Native
{
    // ---- 窗口消息 ----
    public const int WM_HOTKEY = 0x0312;
    public const int WM_NCHITTEST = 0x0084;

    // ---- 键盘修饰键 ----
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // ---- ShowWindow ----
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    // ---- SetWindowPos ----
    public const int SWP_NOSIZE = 0x0001;
    public const int SWP_NOMOVE = 0x0002;
    public const int SWP_NOACTIVATE = 0x0010;
    public const int SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    // ---- GetWindowLong ----
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // ---- 窗口属性 ----
    public const uint GW_OWNER = 4;
    public const uint GA_ROOT = 2;

    // ---- DWM ----
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // ---- 鼠标事件 ----
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// 阻塞直到本线程消息队列有新消息。
    /// 用于替代 while { DoEvents(); Sleep(12); } 的忙等，可消除空转 CPU 占用。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool WaitMessage();

    public const uint QS_ALLINPUT = 0x04FF;

    /// <summary>
    /// 等待消息或超时（超时期间线程休眠，不占 CPU）。
    /// 相比 WaitMessage 可以周期性重新检查外部状态，避免会话挂死。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjects(
        uint nCount, IntPtr pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

    public const uint PM_NOREMOVE = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    /// <summary>窥探消息队列中是否有待处理消息（不取出）。</summary>
    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    /// <summary>查询键的物理按下状态（用于拖拽兜底判定，不依赖 MouseUp 消息）。</summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;

    /// <summary>鼠标左键当前是否处于按下状态。</summary>
    public static bool IsLeftButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    public static bool IsRightButtonDown() => (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

    // ---- 低级鼠标钩子 ----

    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;

    /// <summary>低级钩子事件里 flags 的"注入输入"标志（SendInput/mouse_event）。</summary>
    public const uint LLMHF_INJECTED = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // ---- GDI 位块传输 ----

    /// <summary>源像素直接覆盖目标（1:1 位块传输，不做任何插值/混合）。</summary>
    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>连分层窗口（透明窗口/阴影）一起抓，抓屏时常用。</summary>
    public const uint CAPTUREBLT = 0x40000000;

    /// <summary>
    /// GDI 位块传输。
    ///
    /// 用途：抓屏时把屏幕 DC 的内容直接拷进内存位图，
    /// 比 GDI+ 的 Graphics.CopyFromScreen 少一层建 DC/格式转换的开销。
    /// （注意：不要用它往 WinForms 的双缓冲 Graphics 上画——
    ///  每次 GetHdc 都会同步整块后台缓冲，实测反而慢一倍以上。）
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>释放 HICON（Icon.FromHandle 不接管所有权，必须显式销毁）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA data);

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    public const uint ABM_GETSTATE = 0x00000004;
    public const uint ABS_AUTOHIDE = 0x00000001;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>在当前鼠标位置模拟滚轮滚动。</summary>
    /// <summary>
    /// 在指定屏幕坐标处滚动。
    ///
    /// 走过的坑：
    ///   1. 只发 SendInput 时，如果目标窗口没有焦点（例如焦点停留在我们自己的工具窗上），
    ///      滚动消息会被丢掉 —— 表现为滚不动、第一步就判"到底"。
    ///   2. 改成向该点下的窗口 PostMessage 也不行：PostMessage 到子窗口**不会向父控件冒泡**，
    ///      而滚动逻辑通常在父容器上，于是照样不动。
    ///
    /// 现在的做法：光标移到该点 → 把该点所属的顶层窗口拿到前台（放弃我们自己的焦点）
    /// → 发真实滚轮输入。真实输入会按 WinForms 的规则沿父链冒泡，能滚的控件自然会响应。
    /// </summary>
    public static void ScrollWheelAt(Point screenPoint, int delta)
    {
        SetCursorPos(screenPoint.X, screenPoint.Y);

        var hwnd = WindowFromPoint(new POINT { X = screenPoint.X, Y = screenPoint.Y });
        if (hwnd != IntPtr.Zero)
        {
            var root = GetAncestor(hwnd, GA_ROOT);
            if (root != IntPtr.Zero) SetForegroundWindow(root);
        }

        Thread.Sleep(30);

        // 滚轮按"格"归一：不够一格（120）的增量，WinForms 等控件会直接忽略，
        // 表现为"发了滚轮但页面纹丝不动"。
        if (delta != 0 && Math.Abs(delta) < 120)
            delta = Math.Sign(delta) * 120;

        ScrollWheel(delta);
    }

    public static void ScrollWheel(int delta)
    {
        var input = new INPUT
        {
            type = 0,
            mi = new MOUSEINPUT
            {
                dwFlags = MOUSEEVENTF_WHEEL,
                mouseData = unchecked((uint)delta),
                dx = 0,
                dy = 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}
