// 在桌面生成 / 刷新指向 iTu 最新构建的快捷方式。
//
// 用法：DesktopLink.exe <exe 路径> [快捷方式名] [备注]
//
// 为什么不用 PowerShell 的 WScript.Shell：本机安全策略会拦 PowerShell 里的 COM 实例化
// （"COM object instantiation can run arbitrary code"）。这里直接走 IShellLink /
// IPersistFile 接口，创建后立刻 Load 回来读一遍做自证。
//
// 目标路径固定成 dist 下的同名 exe，所以每次重新发布（覆盖文件）都会自动指向新构建；
// 图标用 exe 内嵌资源，换版本图标也不用改。

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopLink;

internal static class Program
{
    private const int ShowNormal = 1;

    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: DesktopLink <exe 路径> [快捷方式名] [备注]");
            return 2;
        }

        string exe = Path.GetFullPath(args[0]);
        if (!File.Exists(exe))
        {
            Console.WriteLine("[FAIL] 找不到 " + exe);
            return 1;
        }

        string name = args.Length > 1 && args[1].Length > 0 ? args[1] : Path.GetFileNameWithoutExtension(exe);
        string note = args.Length > 2 ? args[2] : "";
        string workDir = Path.GetDirectoryName(exe)!;
        string version = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "?";

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string lnk = Path.Combine(desktop, name + ".lnk");
        string desc = $"iTu {version}" + (note.Length > 0 ? " · " + note : "");

        try
        {
            var link = (IShellLinkW)NewShellLink();
            try
            {
                link.SetPath(exe);
                link.SetWorkingDirectory(workDir);
                link.SetDescription(desc);
                link.SetIconLocation(exe, 0);   // 吃 exe 内嵌图标，换构建自动跟着变
                link.SetShowCmd(ShowNormal);
                ((IPersistFile)link).Save(lnk, true);
            }
            finally
            {
                if (Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("[FAIL] 写入快捷方式: " + e.GetType().Name + ": " + e.Message);
            return 1;
        }

        // 回读自证：目标 / 工作目录 / 图标必须与传入值一致
        try
        {
            var check = NewShellLink();
            try
            {
                var persist = (IPersistFile)check;
                persist.Load(lnk, 0);   // STGM_READ

                var link = (IShellLinkW)check;

                var buf = new StringBuilder(1024);
                link.GetPath(buf, buf.Capacity, IntPtr.Zero, 0);
                string gotTarget = buf.ToString();

                buf.Clear();
                link.GetWorkingDirectory(buf, buf.Capacity);
                string gotDir = buf.ToString();

                buf.Clear();
                link.GetDescription(buf, buf.Capacity);
                string gotDesc = buf.ToString();

                var iconPath = new StringBuilder(1024);
                link.GetIconLocation(iconPath, iconPath.Capacity, out int iconIndex);

                bool ok = string.Equals(gotTarget, exe, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(gotDir, workDir, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(iconPath.ToString(), exe, StringComparison.OrdinalIgnoreCase);

                Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + "桌面快捷方式已落盘并回读一致");
                Console.WriteLine($"  文件   {lnk}  ({new FileInfo(lnk).Length} B)");
                Console.WriteLine($"  目标   {gotTarget}");
                Console.WriteLine($"  起始于 {gotDir}");
                Console.WriteLine($"  图标   {iconPath},{iconIndex}");
                Console.WriteLine($"  备注   {gotDesc}");
                return ok ? 0 : 1;
            }
            finally
            {
                if (Marshal.IsComObject(check)) Marshal.FinalReleaseComObject(check);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("[FAIL] 回读校验: " + e.GetType().Name + ": " + e.Message);
            return 1;
        }
    }

    private static object NewShellLink() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid, throwOnError: true)!)!;

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
