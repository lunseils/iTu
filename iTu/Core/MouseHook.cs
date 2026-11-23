using System.Runtime.InteropServices;

namespace iTu.Core;

/// <summary>一次低级鼠标事件的载荷。</summary>
/// <param name="Position">屏幕物理坐标。</param>
/// <param name="Injected">是否由 SendInput/mouse_event 注入（真实硬件为 false）。</param>
public readonly record struct MouseHookEvent(Point Position, bool Injected);

/// <summary>
/// 全局低级鼠标钩子（WH_MOUSE_LL）。
///
/// 为什么用它：
///   截图浮层需要"按住拖拽"的确定行为。依赖窗口消息（WM_LBUTTONDOWN 等）
///   会遇到：窗口被其它置顶窗口遮挡、鼠标捕获丢失、DPI 下命中判定偏移、
///   跨显示器拖动等一堆情况，任何一种都会让拖拽失效。
///   低级钩子在系统层面先于窗口收到事件，与窗口堆叠/焦点/命中测试完全无关。
///
/// 注意：
///   · 回调在安装钩子的线程上执行，该线程必须有消息循环（本程序有）；
///   · 回调里只做轻量赋值，不能做耗时操作，否则会拖慢全系统鼠标响应；
///     一旦超时（默认 300ms），Windows 会**静默丢弃**该事件，屡次超时还会
///     把钩子从链条里摘掉——这也是浮层侧必须再有一条独立输入通道的原因；
///   · 委托必须由调用方持有引用，否则会被 GC 回收导致进程崩溃。
/// </summary>
public sealed class MouseHook : IDisposable
{
    /// <summary>左键按下（屏幕物理坐标）。</summary>
    public event Action<MouseHookEvent>? Down;

    /// <summary>鼠标移动（屏幕物理坐标）。</summary>
    public event Action<MouseHookEvent>? Move;

    /// <summary>左键松开（屏幕物理坐标）。</summary>
    public event Action<MouseHookEvent>? Up;

    /// <summary>右键按下（屏幕物理坐标）：截图界面里用来"随处右键退出"。</summary>
    public event Action<MouseHookEvent>? RightDown;

    private Native.LowLevelMouseProc? _proc;   // 必须持有，防止 GC
    private IntPtr _hook;
    private bool _disposed;

    // 低级钩子回调对耗时极敏感（超时会被系统静默摘链）。
    // _inCallback 防止嵌套消息泵导致同一钩子重入（例如订阅者里弹模态框时）；
    // 同时记录单次回调耗时，超过阈值写日志，便于定位钩子栈上的重活。
    private bool _inCallback;
    private const long SlowCallbackMs = 40;
    private long _slowCallbackWarned;

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>已分发的鼠标事件计数（诊断用：为 0 说明钩子没收到任何输入）。</summary>
    public int EventCount { get; private set; }

    public void Install()
    {
        if (_disposed) return;                 // Dispose 后不可复用，避免"装了却静默不分发事件"
        if (_hook != IntPtr.Zero) return;

        _proc = HookProc;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, IntPtr.Zero, 0);

        if (_hook == IntPtr.Zero)
            Log.Info("[hook] 低级鼠标钩子安装失败，错误码=" + Marshal.GetLastWin32Error());
        else
            Log.Info("[hook] 低级鼠标钩子安装成功 handle=" + _hook);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed && !_inCallback)
        {
            // 防重入：嵌套消息泵（模态框）会在同一钩子上重入；此时订阅者状态机
            // 尚未处理完上一事件，重入只会带来错乱。直接放过，交回 CallNextHookEx。
            _inCallback = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                var evt = new MouseHookEvent(
                    new Point(info.pt.X, info.pt.Y),
                    (info.flags & Native.LLMHF_INJECTED) != 0);
                EventCount++;

                switch (wParam.ToInt32())
                {
                    case Native.WM_LBUTTONDOWN: Down?.Invoke(evt); break;
                    case Native.WM_MOUSEMOVE: Move?.Invoke(evt); break;
                    case Native.WM_LBUTTONUP: Up?.Invoke(evt); break;
                    case Native.WM_RBUTTONDOWN: RightDown?.Invoke(evt); break;
                }
            }
            catch (Exception ex)
            {
                // 钩子回调内绝不能抛异常，否则会连累整个输入栈；
                // 但也不能完全静默——写日志便于确认"事件到了但处理炸了"。
                Log.Info("[hook] 回调异常: " + ex.Message);
            }
            finally
            {
                sw.Stop();
                // 采样告警：首次超时 + 之后每隔一段记录一次，避免刷屏。
                if (sw.ElapsedMilliseconds >= SlowCallbackMs &&
                    _slowCallbackWarned + 5000 < Environment.TickCount64)
                {
                    _slowCallbackWarned = Environment.TickCount64;
                    Log.Info($"[hook] 回调耗时 {sw.ElapsedMilliseconds}ms（钩子栈上有重活，可能被系统摘链）");
                }
                _inCallback = false;
            }
        }
        // 无论是否处理，都必须立刻把事件交还给钩子链，避免拖慢全系统输入。
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
    }
}
