using System.Drawing;
using System.Runtime.InteropServices;
static class N
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? c, string? t);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
static class P
{
    static void Click(int x, int y, int ms = 80)
    {
        N.SetCursorPos(x, y); System.Threading.Thread.Sleep(160);
        N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(80);
        N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); System.Threading.Thread.Sleep(200);
    }
    [STAThread]
    static void Main()
    {
        var sink = N.FindWindow(null, "iTuHotkeySink");
        if (sink == IntPtr.Zero) { Console.WriteLine("[SKIP]"); return; }
        N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
        System.Threading.Thread.Sleep(1300);
        N.SetCursorPos(300, 200); System.Threading.Thread.Sleep(150);
        N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        for (int i = 1; i <= 12; i++) { N.SetCursorPos(300 + i * 66, 200 + i * 41); System.Threading.Thread.Sleep(28); }
        N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(800);
        var sel = new Rectangle(300, 200, 792, 492);
        int px = sel.Left + (sel.Width - 595) / 2, py = sel.Bottom + 16;
        Click(px + 20, py + 19);
        System.Threading.Thread.Sleep(600);
        var grab = new Rectangle(sel.Left - 40, sel.Top - 140, sel.Width + 80, sel.Height + 240);
        using (var shot = new Bitmap(grab.Width, grab.Height))
        {
            using var g = Graphics.FromImage(shot);
            g.CopyFromScreen(grab.Left, grab.Top, 0, 0, shot.Size);
            shot.Save(@"C:/Users/openwrt/WorkBuddy/2026-11-17-16-34-08/verify/fly3.png");
        }
        Console.WriteLine("[OK]");
    }
}
