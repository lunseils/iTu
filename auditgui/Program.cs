using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

static class N
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? c, string? t);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();

    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
}

static class P
{
    const string OutDir = @"C:/Users/openwrt/WorkBuddy/2026-11-17-16-34-08/verify/";

    static void Click(int x, int y)
    {
        N.SetCursorPos(x, y); Thread.Sleep(140);
        N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(70);
        N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); Thread.Sleep(220);
    }

    static void Drag(int x1, int y1, int x2, int y2, int steps = 10)
    {
        N.SetCursorPos(x1, y1); Thread.Sleep(140);
        N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(70);
        for (int i = 1; i <= steps; i++)
        {
            N.SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
            Thread.Sleep(22);
        }
        N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); Thread.Sleep(260);
    }

    static void Grab(string name, Rectangle r)
    {
        using var bmp = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        bmp.Save(OutDir + name);
    }

    static void Press(byte vk)
    {
        N.keybd_event(vk, 0, 0, IntPtr.Zero); Thread.Sleep(50);
        N.keybd_event(vk, 0, 2, IntPtr.Zero); Thread.Sleep(150);
    }

    static IntPtr FindVisibleDialog()
    {
        IntPtr result = IntPtr.Zero;
        N.EnumWindows((h, _) =>
        {
            if (!N.IsWindowVisible(h)) return true;
            var sb = new StringBuilder(64);
            N.GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() == "#32770") { result = h; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    [STAThread]
    static void Main()
    {
        var sink = N.FindWindow(null, "iTuHotkeySink");
        if (sink == IntPtr.Zero) { Console.WriteLine("[SKIP] iTu 未运行"); return; }

        N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
        Thread.Sleep(1300);

        // 选区 (300,200)-(900,600)
        Drag(300, 200, 900, 600, 14);
        Thread.Sleep(500);

        // 主胶囊：x=314 y=616，基准宽 595、当前宽 572
        int pillX = 314, pillY = 616;
        float s = 572 / 595f;
        var area = new Rectangle(280, 180, 720, 540); // 含选区、胶囊、次级胶囊、颜色弹层

        // 1) 点“形状”→ 次级胶囊必须画在同一分层窗口里
        Click(pillX + (int)(20 * s), pillY + (int)(19 * s));
        Thread.Sleep(450);
        Grab("layered_1_flyout.png", area);

        // 2) 点次级胶囊第二个工具（椭圆），确认同层命中切换
        Click(371, 679);
        Thread.Sleep(350);
        Grab("layered_2_tool.png", area);

        // 3) 点次级胶囊尾部色块 → 同层颜色弹层
        Click(574, 679);
        Thread.Sleep(400);
        Grab("layered_3_picker.png", area);

        // 4) 点弹层第 3 个色块（蓝）→ 颜色应用且弹层收起
        Click(703, 685);
        Thread.Sleep(350);
        Grab("layered_4_color_applied.png", area);

        // 5) 再开弹层，点“自定义…”→ 系统颜色对话框必须盖在浮层之上
        Click(574, 679);
        Thread.Sleep(300);
        Click(896, 685);
        Thread.Sleep(700);
        Grab("layered_5_custom_dialog.png", new Rectangle(760, 480, 900, 560));
        Console.WriteLine("[DIALOG] handle=" + FindVisibleDialog() + " foreground=" + N.GetForegroundWindow());
        Press(0x0D); // Enter = “确定”，走完整提交路径
        Thread.Sleep(400);
        Console.WriteLine("[AFTER_OK] dialog=" + FindVisibleDialog());
        Grab("layered_6_custom_applied.png", area);

        // 6) 取消，退出截图态
        Click(pillX + (int)(539 * s), pillY + (int)(19 * s));
        Thread.Sleep(300);
        Grab("layered_7_after_cancel.png", area);
        Console.WriteLine("[OK]");
    }
}
