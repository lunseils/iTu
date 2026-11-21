using System.Runtime.InteropServices;

namespace GuiTest;

// ---- 剪贴板（绕过输入法）----
internal static class Clip
{
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint fmt, IntPtr hMem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);

    public static void SetText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(50); continue; }
            try
            {
                EmptyClipboard();
                int bytes = (text.Length + 1) * 2;
                var h = GlobalAlloc(0x0042, (UIntPtr)bytes);
                if (h == IntPtr.Zero) return;
                var p = GlobalLock(h);
                Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                Marshal.WriteInt16(p, text.Length * 2, 0);
                GlobalUnlock(h);
                SetClipboardData(13, h);   // CF_UNICODETEXT
                return;
            }
            finally { CloseClipboard(); }
        }
    }
}

