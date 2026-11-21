using System.Runtime.InteropServices;

namespace GuiTest;

internal static class P
{
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    public const uint GR_GDIOBJECTS = 0;
    public const uint GR_USEROBJECTS = 1;
}
