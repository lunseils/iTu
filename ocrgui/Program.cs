using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

// 真机定向验证：截图界面胶囊上的格位、录制面板的全部操作。
//
// 用法：OcrGui.exe ocr | translate | redo | flyout | picker | picker-right
//                  | picker-dismiss | ocr-none | ocr-apply | translate-apply | top-bar
//                  | quickbar | barmenu | barcollapse | rcancel | bottombar | windows
//                  | record | record-builtin | record-mp4 | record-esc | shot
//   1) 起一个记事本，写入已知文字，摆到固定位置（截图区域里有确定的文本）
//   2) 起 iTu，投递截图热键，拖出选区
//   3) 点胶囊上对应的格位
//   4) 等 OCR 回来，整屏抓图 + 打印日志里的 [ocr] 行
//
// 之所以要真机跑：这两个格位此前落进 default 分支，点了没有任何反应，
// 纯逻辑测试看不出来，只有"点下去 → 有没有面板/有没有识别结果"能证明。

static class N
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? c, string? t);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);

    public struct POINT { public int X, Y; }

    /// <summary>
    /// 让记事本以固定矩形**真的站在最前面**。
    ///
    /// 踩过的坑：只 SetWindowPos(HWND_TOP) 时，恢复出来的窗口仍可能压在全屏浏览器底下 ——
    /// 后者的最大化窗口在 z 序更上面，于是"整窗高亮"量到的是浏览器、"翻译"截到的是地址栏。
    /// 这里用 TOPMOST→NOTOPMOST 走一趟：先绝对置顶，再退回普通层，
    /// 位置不变但稳稳落在所有普通窗口之上（不需要前台权限）。
    /// </summary>
    public static bool StageNotepad(IntPtr h, int w, int hgt, int probeX = -1, int probeY = -1)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ShowWindow(h, 9);                       // SW_RESTORE
            Thread.Sleep(120);
            SetWindowPos(h, new IntPtr(-1), 0, 0, w, hgt, 0x0040);   // HWND_TOPMOST + SHOWWINDOW
            Thread.Sleep(160);
            SetWindowPos(h, new IntPtr(-2), 0, 0, w, hgt, 0x0040);   // HWND_NOTOPMOST
            BringWindowToTop(h);
            SetForegroundWindow(h);
            Thread.Sleep(160);

            if (probeX < 0) return true;

            var hit = WindowFromPoint(new POINT { X = probeX, Y = probeY });
            // 命中的可能是记事本里的编辑框子窗口（class=Edit），要往上找到顶层窗口再比类名
            var root = GetAncestor(hit, 2);        // GA_ROOT
            var cls = new StringBuilder(64);
            GetClassName(root == IntPtr.Zero ? hit : root, cls, cls.Capacity);
            if (cls.ToString().Contains("Notepad", StringComparison.OrdinalIgnoreCase)) return true;
            Console.WriteLine($"[stage] 第 {attempt + 1} 次置顶后命中的是 {cls}，重试");
        }
        return false;
    }

    /// <summary>
    /// 把任意窗口顶到普通窗口最上层（不改位置/尺寸）。
    /// 截图前必须走一趟：窗口在别的窗口底下时，抓屏抓到的是盖在上面的那个，
    /// "设置面板"截图就曾经抓成了后台的聊天窗口。
    /// </summary>
    public static void StageWindow(IntPtr h)
    {
        ShowWindow(h, 9);
        Thread.Sleep(120);
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);  // TOPMOST + NOMOVE + NOSIZE + SHOW
        Thread.Sleep(180);
        SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);  // NOTOPMOST
        BringWindowToTop(h);
        SetForegroundWindow(h);
        Thread.Sleep(250);
    }

    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
}

static class P
{
    const string Root = @"C:/Users/openwrt/WorkBuddy/2026-11-17-16-34-08/";
    const string OutDir = Root + "verify/";
    const string Exe = Root + "QqShot/bin/Debug/net10.0-windows/iTu.exe";
    const string Sample = Root + "verify/ocr_sample.txt";

    // 选区与胶囊位置（胶囊底边居中、在选区下方 16px，见 OverlaySession.LayoutChrome）
    // 选区必须真的罩住记事本里的文字：文本从 y≈55 开始，到 y≈130 结束。
    const int SelL = 60, SelT = 48, SelR = 760, SelB = 188;

    static void Click(int x, int y)
    {
        N.SetCursorPos(x, y); Thread.Sleep(160);
        N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(80);
        N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); Thread.Sleep(260);
    }

    static void Click(Point p) => Click(p.X, p.Y);

    static void RightClick(int x, int y)
    {
        N.SetCursorPos(x, y); Thread.Sleep(180);
        N.mouse_event(0x0008, 0, 0, 0, IntPtr.Zero); Thread.Sleep(90);
        N.mouse_event(0x0010, 0, 0, 0, IntPtr.Zero); Thread.Sleep(300);
    }

    static void Drag(int x1, int y1, int x2, int y2, int steps = 14)
    {
        N.SetCursorPos(x1, y1); Thread.Sleep(180);
        N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(90);
        for (int i = 1; i <= steps; i++)
        {
            N.SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
            Thread.Sleep(24);
        }
        N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); Thread.Sleep(320);
    }

    static void Grab(string name, Rectangle r)
    {
        using var bmp = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        bmp.Save(name);
    }

    /// <summary>抓一小块到临时文件并返回路径（用于像素判定，不留在 verify 里）。</summary>
    static string GrabTemp(Rectangle r)
    {
        var path = Path.Combine(Path.GetTempPath(), "probe.png");
        Grab(path, r);
        return path;
    }

    /// <summary>接近纯白的像素数：功能条那张白卡在 = 上万，被遮罩盖住 = 接近 0。</summary>
    static int CountBright(string path, int threshold = 205)
    {
        using var bmp = new Bitmap(path);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R > threshold && c.G > threshold && c.B > threshold) n++;
            }
        return n;
    }

    static IntPtr FindByClass(string cls)    {
        IntPtr result = IntPtr.Zero;
        N.EnumWindows((h, _) =>
        {
            if (!N.IsWindowVisible(h)) return true;
            var sb = new StringBuilder(128);
            N.GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() != cls) return true;
            result = h;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>数一张图里的"标注红"像素（默认画笔色 240,118,114），用来证明笔画在/不在。</summary>
    static int CountInk(string path)
    {
        using var bmp = new Bitmap(path);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R > 190 && c.G is > 60 and < 180 && c.B is > 60 and < 180) n++;
            }
        return n;
    }

    [STAThread]
    static void Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "ocr";
        bool translate = mode == "translate";
        // 可选用第二个参数指定被测 exe（默认 Debug 构建，传 dist 里的产物即为发布包验收）
        string exe = args.Length > 1 ? args[1] : Exe;

        if (mode == "record") { RunRecord(exe); return; }
        if (mode == "record-builtin") { RunCodec(exe, builtinGif: true, mp4: false); return; }
        if (mode == "record-mp4") { RunCodec(exe, builtinGif: false, mp4: true); return; }
        if (mode == "record-esc") { RunCodec(exe, builtinGif: false, mp4: false, escStop: true); return; }

        // 纯截图：shot <png> [x y w h]，用来核对桌面图标之类的界面效果
        if (mode == "shot")
        {
            string outPath = args.Length > 1 ? args[1] : OutDir + "shot.png";
            var rect = args.Length >= 6
                ? new Rectangle(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]))
                : new Rectangle(0, 0, 900, 520);
            Grab(outPath, rect);
            Console.WriteLine("[OK] " + outPath);
            return;
        }

        if (mode == "settings")
        {
            // 直接看设置面板：iTu --settings，等窗口出来再整窗截图
            var settingsApp = Process.Start(exe, "--settings")!;
            Thread.Sleep(1800);
            Rectangle? win = null;
            IntPtr winHandle = IntPtr.Zero;
            N.EnumWindows((h, _) =>
            {
                N.GetWindowThreadProcessId(h, out uint pid);
                if (pid != (uint)settingsApp.Id) return true;
                if (!N.IsWindowVisible(h)) return true;
                N.GetWindowRect(h, out var r);
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (rect.Width > 400 && rect.Height > 400) { win = rect; winHandle = h; }
                return true;
            }, IntPtr.Zero);

            if (win is { } wr)
            {
                // 先顶到最前面再抓，否则抓到的是压在它上面的窗口
                if (winHandle != IntPtr.Zero) N.StageWindow(winHandle);
                N.GetWindowRect(winHandle, out var r2);
                wr = Rectangle.FromLTRB(r2.Left, r2.Top, r2.Right, r2.Bottom);
                Console.WriteLine($"[PASS] 设置面板已打开 {wr}");
                Grab(OutDir + "settings_1.png", wr);
            }
            else
            {
                Console.WriteLine("[FAIL] 没找到设置面板窗口");
            }

            try { settingsApp.Kill(); } catch { }
            Console.WriteLine("[OK] settings");
            return;
        }

        // 1) 记事本 + 已知文本
        File.WriteAllText(Sample,
            "iTu OCR TEST" + Environment.NewLine +
            "HELLO 12345" + Environment.NewLine +
            "The quick brown fox" + Environment.NewLine +
            "jumps over the lazy dog" + Environment.NewLine,
            new UTF8Encoding(true));

        // 旧实例一律清掉：Win11 记事本是单进程多标签，残留窗口会让 FindByClass 摸到一个
        // 不可见的老句柄，置顶半天置的不是用户看得见的那一个。
        foreach (var p in Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch { } }
        Thread.Sleep(300);

        var note = Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Sample + "\""))!;
        Thread.Sleep(1600);
        var hNote = FindByClass("Notepad");
        if (hNote == IntPtr.Zero) { Console.WriteLine("[SKIP] 找不到记事本窗口"); return; }
        bool staged = N.StageNotepad(hNote, 900, 560, 300, 300);
        N.GetWindowRect(hNote, out var nr);
        Console.WriteLine($"[stage] 记事本 {nr.Left},{nr.Top} {nr.Right - nr.Left}x{nr.Bottom - nr.Top}" +
                          $" 命中记事本={staged}");

        // 2) iTu
        foreach (var p in Process.GetProcessesByName("iTu")) { try { p.Kill(); } catch { } }
        Thread.Sleep(400);
        File.Delete(Path.Combine(Path.GetTempPath(), "itu.log"));
        var app = Process.Start(exe)!;
        Thread.Sleep(2200);

        // iTu 起来（浮层是 topmost）之后再压一次记事本：这时探针会被浮层挡住，
        // 所以只置顶不验证 —— 目的是保证随后那次桌面快照里记事本压在最上层，
        // 否则翻译用例会截到浏览器的地址栏、窗口用例会高亮到整个浏览器窗口。
        N.StageNotepad(hNote, 900, 560);

        // 走"再次启动即唤起截图"的正式路径：WM_ACTIVATE_APP 发给标题为 iTu 的激活窗口。
        // 不用 WM_HOTKEY 注入，是因为本机 Ctrl+Alt+A 可能被别的程序占着（注册失败时
        // HotkeyManager 不认识该 id，注入会被丢弃），那条路验证不了界面本身。
        var sink = N.FindWindow(null, "iTu");
        if (sink == IntPtr.Zero) sink = N.FindWindow(null, "iTuHotkeySink");
        if (sink == IntPtr.Zero) { Console.WriteLine("[SKIP] iTu 未就绪"); return; }

        N.PostMessage(sink, 0x8001, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(1300);

        // 这两条用例验证的正是"还没框出选区"时的行为（整窗悬停高亮、顶部功能条），
        // 必须排在任何拖选之前 —— 一旦有了选区，整窗高亮会按设计让位，量不到了。
        if (mode == "quickbar") { RunQuickBar(app, note); return; }
        if (mode == "barmenu") { RunQuickBarMenu(app, note); return; }
        if (mode == "barcollapse") { RunQuickBarCollapse(app, note); return; }
        if (mode == "rcancel") { RunRightCancel(app, note); return; }
        if (mode == "bottombar") { RunBottomBar(app, note); return; }
        if (mode == "small") { RunSmallSelection(app, note); return; }
        if (mode == "cornerbar") { RunCornerStability(app, note); return; }
        if (mode == "barmove") { RunBarMove(app, note); return; }
        if (mode == "barresize") { RunBarResize(app, note); return; }
        if (mode == "windows") { RunWindowPick(app, note); return; }

        // 3) 拖选区
        Drag(SelL, SelT, SelR, SelB);
        Thread.Sleep(500);

        // 4) 点胶囊格位
        // 胶囊尺寸固定（需求 10）：宽度永远是 595、缩放系数恒为 1，
        // 不再随选区宽度缩小 —— 格位坐标可以直接用基准值。
        int selW = SelR - SelL;
        int pillW = 595;
        float s = 1f;
        int pillX = SelL + (selW - pillW) / 2;
        int pillY = SelB + 16;
        int cellX = (int)Math.Round((translate ? 332 : 294) * s);   // 三处格位：文字提取 294 / 翻译 332

        Console.WriteLine($"[geom] 选区={SelL},{SelT}-{SelR},{SelB} 胶囊={pillX},{pillY} w={pillW} s={s:F3}");
        Grab(OutDir + $"{mode}_0_selection.png", new Rectangle(0, 0, 1000, 460));

        if (mode == "redo")
        {
            // 画笔 → 画一笔 → 撤销 → 重做，四张图 + 逐张数红色像素
            Click(pillX + (int)Math.Round(96 * s), pillY + (int)(19 * s));   // 画笔
            Thread.Sleep(300);
            Drag(200, 90, 520, 150, 12);
            Thread.Sleep(350);
            Grab(OutDir + "redo_1_drawn.png", new Rectangle(40, 30, 760, 200));
            Console.WriteLine("[redo] 画完 红像素=" + CountInk(OutDir + "redo_1_drawn.png"));

            Click(pillX + (int)Math.Round(388 * s), pillY + (int)(19 * s));  // 撤销
            Thread.Sleep(350);
            Grab(OutDir + "redo_2_undone.png", new Rectangle(40, 30, 760, 200));
            Console.WriteLine("[redo] 撤销后 红像素=" + CountInk(OutDir + "redo_2_undone.png"));

            Click(pillX + (int)Math.Round(427 * s), pillY + (int)(19 * s));  // 重做
            Thread.Sleep(350);
            Grab(OutDir + "redo_3_redone.png", new Rectangle(40, 30, 760, 200));
            Console.WriteLine("[redo] 重做后 红像素=" + CountInk(OutDir + "redo_3_redone.png"));

            Click(pillX + (int)Math.Round(539 * s), pillY + (int)(19 * s));  // 取消
            Thread.Sleep(300);

            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            Console.WriteLine("[OK] redo");
            return;
        }

        if (mode == "flyout") { RunFlyout(app, note, pillX, pillY, s, selW); return; }
        if (mode == "picker") { RunPicker(app, note, pillX, pillY, s, selW); return; }
        if (mode == "picker-right") { RunPickerRight(app, note, pillX, pillY, s, selW); return; }
        if (mode == "picker-dismiss") { RunPickerDismiss(app, note, pillX, pillY, s); return; }
        if (mode == "ocr-none") { RunOcrNone(app, note); return; }
        if (mode == "ocr-apply") { RunOcrApply(app, note, pillX, pillY, s); return; }
        if (mode == "translate-apply") { RunTranslateApply(app, note, pillX, pillY, s); return; }
        if (mode == "top-bar") { RunTopBar(app, note); return; }

        Click(pillX + cellX, pillY + (int)(19 * s));

        // 5) 等 OCR（系统 OCR 首次调用要起 PowerShell，给足时间）
        Thread.Sleep(translate ? 9000 : 6000);
        Grab(OutDir + $"ocr_{mode}_1_panel.png", new Rectangle(0, 0, 1000, 560));

        Console.WriteLine("[log]");
        var logPath = Path.Combine(Path.GetTempPath(), "itu.log");
        if (File.Exists(logPath))
            foreach (var line in File.ReadAllLines(logPath))
                if (line.Contains("[ocr]") || line.Contains("会话结束") || line.Contains("overlay"))
                    Console.WriteLine("  " + line);

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] " + mode);
    }

    // ================= 形状次级胶囊的调节条验收 =================
    //
    // 记录的问题：形状次级胶囊里的"粗细"调节条
    //   A 排不下：三个工具 + 描边/填充 + 标签 + 滑杆 + 数值 + 色块 需要 ~350px，
    //     而飞层宽度固定取主胶囊一半（595/2≈297），滑杆被挤压后直接压在色块上，
    //     数值文字画到色块右边被裁掉。
    //   B 拖不动：指针一旦离开飞层矩形，OverlaySession 就不再转发 PointerMove，
    //     拖拽状态被清掉，值停在半路；更糟的是事件继续往下走，变成重新框选。
    //
    // 所以这里做两件事：读日志里的布局矩形做几何断言；再真的按住滑杆拖到飞层外面。

    static Dictionary<string, Rectangle> ParseFlyout()
    {
        var map = new Dictionary<string, Rectangle>();
        var log = Path.Combine(Path.GetTempPath(), "itu.log");
        if (!File.Exists(log)) return map;

        string? last = null;
        foreach (var line in File.ReadAllLines(log))
            if (line.Contains("[flyout] bounds=")) last = line;
        if (last == null) return map;

        var m = System.Text.RegularExpressions.Regex.Match(last, @"bounds=(-?\d+),(-?\d+),(\d+),(\d+)");
        if (m.Success) map["bounds"] = Rect(m);

        var ml = System.Text.RegularExpressions.Regex.Match(last, @"label=(-?\d+),(-?\d+),(\d+),(\d+)");
        if (ml.Success) map["label"] = Rect(ml);
        var mv = System.Text.RegularExpressions.Regex.Match(last, @"value=(-?\d+),(-?\d+),(\d+),(\d+)");
        if (mv.Success) map["value"] = Rect(mv);

        foreach (var name in new[] { "Slider#0", "Swatch#0", "Fill#0", "Fill#1", "Tool#0", "Tool#2" })
        {
            var mm = System.Text.RegularExpressions.Regex.Match(last, name + @"=(-?\d+),(-?\d+),(\d+),(\d+)");
            if (mm.Success) map[name] = Rect(mm);
        }
        return map;
    }

    static int LastSliderValue()
    {
        var log = Path.Combine(Path.GetTempPath(), "itu.log");
        if (!File.Exists(log)) return -1;
        int v = -1;
        foreach (var line in File.ReadAllLines(log))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"\[flyout\] slider=(\d+)");
            if (m.Success) v = int.Parse(m.Groups[1].Value);
        }
        return v;
    }

    static void RunFlyout(Process app, Process note, int pillX, int pillY, float s, int selW)
    {
        Click(pillX + (int)Math.Round(20 * s), pillY + (int)(19 * s));   // 主胶囊「形状」
        Thread.Sleep(450);

        var geo = ParseFlyout();
        if (!geo.ContainsKey("bounds") || !geo.ContainsKey("Slider#0"))
        {
            Console.WriteLine("[FAIL] 飞层没有出现（日志里没有 [flyout] bounds）");
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        var fly = geo["bounds"];
        var slider = geo["Slider#0"];
        Console.WriteLine($"[geom] 飞层={fly.X},{fly.Y},{fly.Width},{fly.Height} " +
                          $"滑杆={slider.X},{slider.Y},{slider.Width},{slider.Height}");

        Grab(OutDir + "flyout_1_opened.png", Padded(fly, 30));

        // A) 滑杆必须完整落在飞层内，且和色块/工具格不重叠
        bool inside = fly.Contains(slider);
        Console.WriteLine((inside ? "[PASS] " : "[FAIL] ") + "滑杆完整落在飞层内");
        if (geo.TryGetValue("Swatch#0", out var swatch))
        {
            var inter = Rectangle.Intersect(slider, swatch);
            Console.WriteLine((inter.Width <= 0 || inter.Height <= 0 ? "[PASS] " : "[FAIL] ") +
                              $"滑杆与色块不重叠 色块={swatch.X},{swatch.Y},{swatch.Width},{swatch.Height}" +
                              (inter.Width > 0 && inter.Height > 0 ? $" 重叠={inter.Width}x{inter.Height}" : ""));

            if (geo.TryGetValue("value", out var val))
                Console.WriteLine((val.Right <= swatch.Left ? "[PASS] " : "[FAIL] ") +
                                  $"数值文字不压色块 数值右={val.Right} 色块左={swatch.Left}");
        }
        if (geo.TryGetValue("Tool#2", out var lastTool))
            Console.WriteLine((lastTool.Right <= slider.Left ? "[PASS] " : "[FAIL] ") +
                              $"最后一个工具格不与滑杆重叠 格右={lastTool.Right} 滑杆左={slider.Left}");

        // B) 按住滑杆往右下拖到飞层外面：值必须一路跟到最大，且不能变成重新框选
        int cy = slider.Y + slider.Height / 2;
        int x0 = slider.X + slider.Width / 5;
        Console.WriteLine("[act] 按住滑杆 → 拖到飞层外（下 90px、右 40px）");
        Drag(x0, cy, fly.Right + 40, fly.Bottom + 90, 16);
        Thread.Sleep(400);

        int v = LastSliderValue();
        Console.WriteLine((v == 24 ? "[PASS] " : "[FAIL] ") + $"拖出飞层后取到最大值 实际={v}");
        Grab(OutDir + "flyout_2_dragged_out.png", Padded(fly, 30));

        // 再反向拖到飞层左外侧：值应回到最小，证明拖出后仍由滑杆接管
        Console.WriteLine("[act] 再次按住 → 拖到飞层左外");
        Drag(slider.Right - 4, cy, fly.Left - 60, fly.Bottom + 40, 16);
        Thread.Sleep(400);
        int v2 = LastSliderValue();
        Console.WriteLine((v2 == 1 ? "[PASS] " : "[FAIL] ") + $"反向拖出飞层后取到最小值 实际={v2}");
        Grab(OutDir + "flyout_3_dragged_back.png", Padded(fly, 30));

        // C) 其它几层（箭头 / 画笔 / 文字 / 马赛克）同样要排得下
        foreach (var (name, cx) in new[] { ("箭头", 59), ("画笔", 96), ("文字", 143), ("马赛克", 213) })
        {
            Click(pillX + (int)Math.Round(cx * s), pillY + (int)(19 * s));
            Thread.Sleep(380);

            var g2 = ParseFlyout();
            if (!g2.ContainsKey("bounds") || !g2.ContainsKey("Slider#0"))
            {
                Console.WriteLine($"[FAIL] {name} 层没有出现");
                continue;
            }

            var fb = g2["bounds"];
            var sl = g2["Slider#0"];
            bool ok = fb.Contains(sl);
            if (g2.TryGetValue("Swatch#0", out var sw))
            {
                var it = Rectangle.Intersect(sl, sw);
                ok &= it.Width <= 0 || it.Height <= 0;
            }
            if (g2.TryGetValue("value", out var vx)) ok &= vx.Right < fb.Right - 8;
            if (g2.TryGetValue("Tool#2", out var t2)) ok &= t2.Right <= sl.Left;

            Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") +
                              $"{name} 层布局 飞层={fb.Width}x{fb.Height} 滑杆={sl.Width}px 滑杆右={sl.Right} 飞层右={fb.Right}");
        }

        Grab(OutDir + "flyout_4_other_layers.png", new Rectangle(0, 0, 1000, 380));

        Click(pillX + (int)Math.Round(539 * s), pillY + (int)(19 * s));   // 取消
        Thread.Sleep(900);

        Console.WriteLine("[log]");
        var logPath = Path.Combine(Path.GetTempPath(), "itu.log");
        if (File.Exists(logPath))
            foreach (var line in File.ReadAllLines(logPath))
                if (line.Contains("[flyout]")) Console.WriteLine("  " + line);

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] flyout");
    }

    // ================= 录制面板验收 =================
    //
    // 三个用例，覆盖面板上所有能点的东西：
    //   A 录 2.5s → 暂停 1s（比对两张面板截图证明"冻结"）→ 继续 → 点"停止并保存"→ 校验产物
    //   B 录 1.5s → 用录制热键停止（等价于托盘「停止录制」/ 再按一次热键）→ 校验产物
    //   C 录 1.5s → 点"取消"→ 校验目录里没有新增/残留文件（半成品必须被删掉）

    static readonly int[] RecSel = { 200, 150, 900, 470 };

    // ================= 自定义颜色框验收 =================
    //
    // 现场：次级胶囊 → 色块 → 弹层里的「自定义…」→ 系统取色框。
    // 需要证明三件事：
    //   A 弹层本身完整可见（它的锚点是飞层右边，很容易顶出屏幕）
    //   B 取色框能出来、能看见（owner 是全屏置顶分层窗口，很容易被压住）
    //   C 在取色框里点鼠标时，截图界面不能被连带操作（输入走全局低级鼠标钩子）

    static Dictionary<string, Rectangle> ParsePicker()
    {
        var map = new Dictionary<string, Rectangle>();
        var log = Path.Combine(Path.GetTempPath(), "itu.log");
        if (!File.Exists(log)) return map;

        string? last = null;
        foreach (var line in File.ReadAllLines(log))
            if (line.Contains("[picker] bounds=")) last = line;
        if (last == null) return map;

        foreach (var name in new[] { "bounds", "card", "button" })
        {
            var m = System.Text.RegularExpressions.Regex.Match(last, name + @"=(-?\d+),(-?\d+),(\d+),(\d+)");
            if (m.Success) map[name] = Rect(m);
        }
        return map;
    }

    /// <summary>找系统取色框（Class=#32770，标题含"颜色"/"Color"）。</summary>
    static IntPtr FindColorDialog()
    {
        IntPtr found = IntPtr.Zero;
        N.EnumWindows((h, _) =>
        {
            if (!N.IsWindowVisible(h)) return true;
            var cls = new StringBuilder(64);
            N.GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "#32770") return true;
            var t = new StringBuilder(256);
            N.GetWindowText(h, t, t.Capacity);
            string title = t.ToString();
            if (!title.Contains("颜色") && !title.Contains("Color")) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    static Rectangle WindowRect(IntPtr h)
    {
        if (h == IntPtr.Zero) return Rectangle.Empty;
        N.GetWindowRect(h, out var r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>从日志里取虚拟桌面尺寸（`虚拟桌面={X=0,Y=0,Width=2560,Height=1440}`），拿不到就退回 1920×1080。</summary>
    static Rectangle VirtualScreen()
    {
        var log = Path.Combine(Path.GetTempPath(), "itu.log");
        if (File.Exists(log))
        {
            Rectangle r = Rectangle.Empty;
            foreach (var line in File.ReadAllLines(log))
            {
                if (line.Contains("虚拟桌面") && TryRect(line, "虚拟桌面", out var a)) r = a;
                else if (TryRect(line, "virtual", out var b)) r = b;
            }
            if (r.Width > 0) return r;
        }
        return new Rectangle(0, 0, 1920, 1080);
    }

    static void RunPicker(Process app, Process note, int pillX, int pillY, float s, int selW)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "itu.log");
        var main = VirtualScreen();
        // 1) 画笔层 → 点色块 → 颜色弹层
        Click(pillX + (int)Math.Round(96 * s), pillY + (int)(19 * s));
        Thread.Sleep(500);

        var fly = ParseFlyout();
        if (!fly.ContainsKey("bounds") || !fly.ContainsKey("Swatch#0"))
        {
            Console.WriteLine("[FAIL] 飞层或色块没有出现");
            Grab(OutDir + "picker_0_no_flyout.png", new Rectangle(0, 0, 1000, 520));
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        Click(Center(fly["Swatch#0"]));
        Thread.Sleep(500);

        var pick = ParsePicker();
        if (!pick.ContainsKey("bounds") || !pick.ContainsKey("button"))
        {
            Console.WriteLine("[FAIL] 颜色弹层没有出现（日志里没有 [picker] bounds）");
            Grab(OutDir + "picker_0_no_picker.png", new Rectangle(0, 0, 1000, 520));
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        var pb = pick["bounds"];
        var btn = pick["button"];
        // 日志里这会儿才有 virtual= 那行，取真实虚拟桌面再判越界
        main = VirtualScreen();
        Console.WriteLine($"[geom] 弹层={pb.X},{pb.Y},{pb.Width},{pb.Height} " +
                          $"自定义按钮={btn.X},{btn.Y},{btn.Width},{btn.Height}");
        Grab(OutDir + "picker_1_open.png", new Rectangle(0, 0, 1000, 520));

        // A) 弹层要完整落在屏幕内
        bool onScreen = main.Contains(pb);
        Console.WriteLine((onScreen ? "[PASS] " : "[FAIL] ") +
                          $"颜色弹层完整在屏内 右={pb.Right} 下={pb.Bottom} 屏幕={main.Width}x{main.Height}");

        // B) 点「自定义…」，看系统取色框能不能出来并被人看见
        Click(Center(btn));
        Thread.Sleep(1500);

        var dlg = FindColorDialog();
        var dRect = WindowRect(dlg);
        bool visible = dlg != IntPtr.Zero && N.IsWindowVisible(dlg);
        Console.WriteLine((visible ? "[PASS] " : "[FAIL] ") +
                          $"自定义颜色框已弹出 handle={dlg} " +
                          $"rect={dRect.X},{dRect.Y},{dRect.Width},{dRect.Height}");
        if (visible)
            Console.WriteLine((main.Contains(dRect) ? "[PASS] " : "[FAIL] ") +
                              $"取色框完整在屏内 右={dRect.Right} 下={dRect.Bottom}");
        Grab(OutDir + "picker_2_dialog.png", new Rectangle(0, 0, 1200, 760));

        // C) 在取色框里真点一下：鼠标事件同样会进全局钩子
        if (visible)
        {
            int before = CountInk(OutDir + "picker_2_dialog.png");
            Click(Center(dRect));
            Thread.Sleep(600);
            Grab(OutDir + "picker_3_click_in_dialog.png", new Rectangle(0, 0, 1200, 760));
            int after = CountInk(OutDir + "picker_3_click_in_dialog.png");
            Console.WriteLine($"[info] 点击取色框前后 标注红像素 {before} → {after}");
        }

        // 关掉取色框（IDOK）
        if (visible)
        {
            N.PostMessage(dlg, 0x0111, (IntPtr)1, IntPtr.Zero);
            Thread.Sleep(1000);
        }
        Grab(OutDir + "picker_4_after.png", new Rectangle(0, 0, 1000, 520));

        // 会话是否被这次操作终结
        var lines = File.Exists(logPath) ? File.ReadAllLines(logPath) : Array.Empty<string>();
        int idx = -1;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains("[picker] bounds=")) idx = i;

        bool ended = false;
        for (int i = idx + 1; i < lines.Length; i++)
            if (lines[i].Contains("会话结束")) ended = true;

        Console.WriteLine((!ended ? "[PASS] " : "[FAIL] ") + "开/关自定义颜色框没有结束截图会话");
        Console.WriteLine("[log]");
        foreach (var line in lines)
            if (line.Contains("[picker]") || line.Contains("[overlay]") || line.Contains("异常"))
                Console.WriteLine("  " + line);

        // D) 取消路径：关框之后输入必须恢复（_modalOpen 没复位的话，界面就像死了）
        Click(Center(fly["Swatch#0"]));
        Thread.Sleep(450);
        var pick2 = ParsePicker();
        if (pick2.ContainsKey("button"))
        {
            Click(Center(pick2["button"]));
            Thread.Sleep(1300);
            var dlg2 = FindColorDialog();
            Console.WriteLine((dlg2 != IntPtr.Zero ? "[PASS] " : "[FAIL] ") + "第二次打开自定义颜色框");
            if (dlg2 != IntPtr.Zero)
            {
                N.PostMessage(dlg2, 0x0111, (IntPtr)2, IntPtr.Zero);   // IDCANCEL
                Thread.Sleep(1000);
            }

            Grab(OutDir + "picker_5_before_draw.png", new Rectangle(40, 30, 760, 220));
            int ink0 = CountInk(OutDir + "picker_5_before_draw.png");

            Drag(200, 90, 460, 150, 12);          // 选区内画一笔
            Thread.Sleep(450);
            Grab(OutDir + "picker_5_after_draw.png", new Rectangle(40, 30, 760, 220));
            int ink1 = CountInk(OutDir + "picker_5_after_draw.png");

            Console.WriteLine((ink1 > ink0 + 100 ? "[PASS] " : "[FAIL] ") +
                              $"取消取色框后仍能继续画 红像素 {ink0} → {ink1}");
        }

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] picker");
    }

    /// <summary>
    /// 选区贴屏幕右边时的弹层定位。
    ///
    /// 历史：
    ///   · 最早锚点固定"飞层右边 +8"、宽度 326px，选区靠右时整块顶出屏幕；
    ///   · 先改成"右侧放不下就翻到左侧"；
    ///   · 现在按需求改成：右侧放不下就落到**次级胶囊下方排 3 行**（色板 + 自定义按钮），
    ///     高度、圆角与次级胶囊同一套语言，且必须完整留在屏幕内。
    /// </summary>
    static void RunPickerRight(Process app, Process note, int pillX, int pillY, float s, int selW)
    {
        const int L = 2050, T = 140, R = 2550, B = 360;
        Drag(L, T, R, B);
        Thread.Sleep(600);

        selW = R - L;
        int pillW = 595;                   // 固定尺寸（需求 10）
        pillX = L + (selW - pillW) / 2;
        pillY = B + 16;
        s = 1f;

        Click(pillX + (int)Math.Round(96 * s), pillY + (int)(19 * s));
        Thread.Sleep(500);

        var fly = ParseFlyout();
        if (!fly.ContainsKey("bounds") || !fly.ContainsKey("Swatch#0"))
        {
            Console.WriteLine("[FAIL] 靠右选区下飞层/色块没出现");
            Grab(OutDir + "pickerR_0_nofly.png", new Rectangle(1500, 100, 1060, 420));
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        Click(Center(fly["Swatch#0"]));
        Thread.Sleep(500);

        var pick = ParsePicker();
        var screen = VirtualScreen();
        if (!pick.ContainsKey("bounds"))
        {
            Console.WriteLine("[FAIL] 靠右选区下弹层没有出现");
            Grab(OutDir + "pickerR_0_nopicker.png", new Rectangle(1500, 100, 1060, 420));
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        var fb = fly["bounds"];
        var pb = pick["bounds"];
        var pillRect = new Rectangle(pillX, pillY, pillW, (int)Math.Round(38 * s));
        Console.WriteLine($"[geom] 飞层={fb.X},{fb.Y},{fb.Width},{fb.Height} " +
                          $"弹层={pb.X},{pb.Y},{pb.Width},{pb.Height} 屏幕右={screen.Right}");

        bool inside = pb.Right <= screen.Right && pb.Left >= screen.Left &&
                      pb.Bottom <= screen.Bottom && pb.Top >= screen.Top;
        Console.WriteLine((inside ? "[PASS] " : "[FAIL] ") +
                          $"靠右时弹层完整在屏内 右={pb.Right} 下={pb.Bottom} 屏幕={screen.Width}x{screen.Height}");

        // 三级控件必须"总共三行"：主胶囊一行、次级胶囊一行、颜色弹层一行。
        // 所以颜色弹层**不能再排成三行**，而且高度要与次级胶囊完全一致。
        var geoLine = LastPickerLine();
        bool oneRow = geoLine != null && geoLine.Contains("mode=row") && geoLine.Contains("rows=1");
        Console.WriteLine((oneRow ? "[PASS] " : "[FAIL] ") +
                          $"颜色弹层是一条胶囊 {(geoLine ?? "（日志里没有 [picker] 行）")}");

        bool sameHeight = Math.Abs(pb.Height - (fb.Height + 10)) <= 2;
        Console.WriteLine((sameHeight ? "[PASS] " : "[FAIL] ") +
                          $"弹层与次级胶囊等高 弹层高={pb.Height} 飞层高={fb.Height}");

        bool notOverFly = !pb.IntersectsWith(fb);
        bool notOverPill = !pb.IntersectsWith(pillRect);
        Console.WriteLine((notOverFly && notOverPill ? "[PASS] " : "[FAIL] ") +
                          $"弹层不压前两级 压飞层={pb.IntersectsWith(fb)} 压胶囊={pb.IntersectsWith(pillRect)}");

        // 需求 5：第三级要跟前两级**同列开头对齐**，也就是落在次级胶囊下方、
        // 左边缘与主胶囊/次级胶囊的左边线重合（bounds 含 5px 阴影外扩，比较时补回来）。
        bool belowPlace = geoLine != null && geoLine.Contains("place=below");
        Console.WriteLine((belowPlace ? "[PASS] " : "[FAIL] ") +
                          $"颜色弹层落在同列下方 {(geoLine ?? "")}");
        int pbLeft = pb.Left + 5;
        bool colAligned = Math.Abs(pbLeft - fb.Left) <= 2;
        Console.WriteLine((colAligned ? "[PASS] " : "[FAIL] ") +
                          $"三级同列开头对齐 弹层左={pbLeft} 飞层左={fb.Left}");

        Grab(OutDir + "pickerR_1_open.png", new Rectangle(1500, 100, 1060, 460));
        Click(pillX + (int)Math.Round(539 * s), pillY + (int)(19 * s));
        Thread.Sleep(600);

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] picker-right");
    }

    /// <summary>最后一条 [picker] 日志行（用于断言布局模式）。</summary>
    static string? LastPickerLine()
    {
        var log = Path.Combine(Path.GetTempPath(), "itu.log");
        if (!File.Exists(log)) return null;
        string? last = null;
        foreach (var line in File.ReadAllLines(log))
            if (line.Contains("[picker] bounds=")) last = line;
        return last;
    }

    /// <summary>
    /// 颜色弹层的自动收起：
    /// 弹着色板去点次级胶囊上的另一个工具时，色板必须自己关掉（并且工具真的切换了）。
    /// 以前只有"选中颜色"或"收起次级胶囊"才会关，色板会一直挂在画面上。
    /// </summary>
    static void RunPickerDismiss(Process app, Process note, int pillX, int pillY, float s)
    {
        Click(pillX + (int)Math.Round(20 * s), pillY + (int)(19 * s));   // 主胶囊「形状」
        Thread.Sleep(450);

        var fly = ParseFlyout();
        if (!fly.ContainsKey("Swatch#0") || !fly.ContainsKey("Tool#2"))
        {
            Console.WriteLine("[FAIL] 形状次级胶囊没有出现（缺 Swatch#0 / Tool#2）");
            Grab(OutDir + "pickerd_0_nofly.png", new Rectangle(40, 30, 1200, 320));
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        Click(Center(fly["Swatch#0"]));
        Thread.Sleep(450);
        if (!ParsePicker().ContainsKey("bounds"))
        {
            Console.WriteLine("[FAIL] 色板没弹出");
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }
        Grab(OutDir + "pickerd_1_open.png", new Rectangle(40, 30, 1400, 320));

        // 不改颜色，直接点次级胶囊上的「直线」
        Click(Center(fly["Tool#2"]));
        Thread.Sleep(500);
        Grab(OutDir + "pickerd_2_after_pick.png", new Rectangle(40, 30, 1400, 320));

        var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>();
        int pickIdx = -1;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains("[picker] bounds=")) pickIdx = i;

        bool dismissed = false;
        bool toolSwitched = false;
        for (int i = pickIdx + 1; i < lines.Length && pickIdx >= 0; i++)
        {
            if (lines[i].Contains("[picker] dismiss reason=外部点击")) dismissed = true;
            if (lines[i].Contains("[tool] active=Line")) toolSwitched = true;
        }

        Console.WriteLine((dismissed ? "[PASS] " : "[FAIL] ") + "点其它工具后色板自动收起");
        Console.WriteLine((toolSwitched ? "[PASS] " : "[FAIL] ") + "点其它工具后工具确实切换（active=Line）");

        // 收起的另一半证据：这一刻画面上不该再有色板（再点一次色块应重新弹出）
        Click(Center(fly["Swatch#0"]));
        Thread.Sleep(450);
        int countBefore = CountPickerOpenings();
        Console.WriteLine((countBefore >= 2 ? "[PASS] " : "[FAIL] ") +
                          $"收起后色块仍能重新弹出色板（[picker] 日志 {countBefore} 条）");

        Click(pillX + (int)Math.Round(539 * s), pillY + (int)(19 * s));
        Thread.Sleep(300);

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] picker-dismiss");
    }

    static string LogPath => Path.Combine(Path.GetTempPath(), "itu.log");

    /// <summary>轮询日志，直到出现包含 needle 的行（最多等 timeoutMs）。</summary>
    static bool WaitForLog(string needle, int timeoutMs)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (File.Exists(LogPath) &&
                File.ReadAllLines(LogPath).Any(l => l.Contains(needle))) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    static int CountPickerOpenings()
    {
        if (!File.Exists(LogPath)) return 0;
        int n = 0;
        foreach (var line in File.ReadAllLines(LogPath))
            if (line.Contains("[picker] bounds=")) n++;
        return n;
    }

    /// <summary>
    /// 截图里没有文字时的识图：
    /// 以前会弹一个写着「未识别到文字」的面板；现在必须**不弹任何窗口**，
    /// 只在「文字提取」格位旁贴一条标签，并且截图界面保持可继续操作。
    /// 做法：重新框一块记事本的空白区域（纯白，OCR 必然没有结果）。
    /// </summary>
    static void RunOcrNone(Process app, Process note)
    {
        // 起点必须在原选区之外，否则会被当成"拖动选区"
        Drag(100, 300, 500, 400);
        Thread.Sleep(500);

        int selW = 400, selL = 100, selT = 300, selB = 400;
        int pillW = 595;                       // 固定尺寸（需求 10）
        float s = 1f;
        int pillX = Math.Max(6, selL + (selW - pillW) / 2);
        int pillY = selB + 16;

        Console.WriteLine($"[geom] 空白选区={selL},{selT}-{selL + selW},{selB} " +
                          $"胶囊={pillX},{pillY} w={pillW} s={s:F3}");
        Grab(OutDir + "ocrnone_0_selection.png", new Rectangle(0, 0, 1000, 560));

        Click(pillX + (int)Math.Round(294 * s), pillY + (int)(19 * s));   // 文字提取
        Thread.Sleep(1200);
        Grab(OutDir + "ocrnone_1_label.png", new Rectangle(0, 0, 1000, 560));   // 标签还在的时刻
        Thread.Sleep(5300);
        Grab(OutDir + "ocrnone_2_after.png", new Rectangle(0, 0, 1000, 560));

        var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>();
        bool noText = lines.Any(l => l.Contains("选区里没有文字"));
        bool success = lines.Any(l => l.Contains("识别成功"));
        bool ended = lines.Any(l => l.Contains("会话结束"));

        Console.WriteLine((noText ? "[PASS] " : "[FAIL] ") + "空白选区判定为没有文字");
        Console.WriteLine((!success ? "[PASS] " : "[FAIL] ") + "没有走「识别成功→弹面板」分支");
        Console.WriteLine((!ended ? "[PASS] " : "[FAIL] ") + "截图界面没有因此结束（标签贴在工具上）");

        Console.WriteLine("[log]");
        foreach (var line in lines)
            if (line.Contains("[text]") || line.Contains("[ocr]") || line.Contains("会话结束"))
                Console.WriteLine("  " + line);

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] ocr-none");
    }

    /// <summary>
    /// 识图要"落在图上"：识别结果原位重写原文，**不弹任何窗口**，会话也不结束。
    ///
    /// 四个断言：
    ///   A 日志出现"已写回选区画面"（走改写画面的路径，而不是弹面板的路径）
    ///   B 会话没有结束（界面还在用户手上，可以接着保存/标注）
    ///   C 整个过程 iTu 没有多出可见窗口（之前左上角那个结果窗就是这么冒出来的）
    ///   D 保存出来的图里文字还在（不是被抹成一片白）
    /// </summary>
    static void RunOcrApply(Process app, Process note, int pillX, int pillY, float s)
    {
        var sel = new Rectangle(SelL, SelT, SelR - SelL, SelB - SelT);
        Grab(OutDir + "ocra_0_before.png", sel);
        int windowsBefore = VisibleAppWindows(app.Id);
        Console.WriteLine($"[info] 点识图前 iTu 可见窗口={windowsBefore}");

        Click(pillX + (int)Math.Round(294 * s), pillY + (int)(19 * s));   // 文字提取

        bool done = WaitForLog("已写回选区画面", 25000) || WaitForLog("识别失败", 1200);
        Thread.Sleep(400);

        int windowsAfter = VisibleAppWindows(app.Id);
        Console.WriteLine($"[info] 识图完成后 iTu 可见窗口={windowsAfter}");
        Grab(OutDir + "ocra_1_after.png", sel);
        Grab(OutDir + "ocra_2_pill_label.png", new Rectangle(0, 150, 1100, 220));

        var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>();
        bool ended = lines.Any(l => l.Contains("会话结束"));
        bool applied = lines.Any(l => l.Contains("已写回选区画面"));
        bool recognized = lines.Any(l => l.Contains("识别成功"));

        Console.WriteLine((done ? "[PASS] " : "[FAIL] ") + "识图有结果（不是静默无响应）");
        Console.WriteLine((recognized && applied ? "[PASS] " : "[FAIL] ") +
                          "识别结果直接写回选区画面（不弹文本面板）");
        Console.WriteLine((!ended ? "[PASS] " : "[FAIL] ") + "识图没有结束截图会话");
        Console.WriteLine((windowsAfter <= windowsBefore ? "[PASS] " : "[FAIL] ") +
                          $"识图过程中没有多出窗口（{windowsBefore} → {windowsAfter}）");

        // 保存，确认落盘的就是"写着文字"的那张图
        var before = NewestSavedFile(SaveDir());
        Click(pillX + (int)Math.Round(501 * s), pillY + (int)(19 * s));   // 保存
        Thread.Sleep(1500);
        var after = NewestSavedFile(SaveDir());
        bool saved = after != null && after != before;
        Console.WriteLine((saved ? "[PASS] " : "[FAIL] ") + $"保存出图 {after ?? "（没有新文件）"}");
        if (saved)
        {
            File.Copy(after!, OutDir + "ocra_3_saved.png", true);
            int darkBefore = CountDark(OutDir + "ocra_0_before.png");
            int darkSaved = CountDark(OutDir + "ocra_3_saved.png");
            bool textKept = darkSaved >= darkBefore * 0.5 && darkSaved <= darkBefore * 2.0;
            Console.WriteLine((textKept ? "[PASS] " : "[FAIL] ") +
                              $"保存的图里文字还在（深色像素 {darkBefore} → {darkSaved}）");
        }

        Console.WriteLine("[log]");
        foreach (var line in lines)
            if (line.Contains("[text]") || line.Contains("[tool]") || line.Contains("会话结束"))
                Console.WriteLine("  " + line);

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] ocr-apply");
    }

    /// <summary>截图贴屏幕顶边时的控件布局：圆角条此前会落到"选区内部 +10px"，压住要识别的文字。</summary>
    static void RunTopBar(Process app, Process note)
    {
        Drag(60, 6, 760, 150);
        Thread.Sleep(700);
        Grab(OutDir + "topbar_0_selection.png", new Rectangle(0, 0, 1000, 420));
        Grab(OutDir + "topbar_1_bar.png", new Rectangle(0, 0, 820, 260));
        // 全景：一眼看清"选区贴顶时，圆角条落在右侧、功能条仍在顶部正中、两者不打架"
        Grab(OutDir + "topbar_wide.png", new Rectangle(0, 0, 1700, 320));

        var map = ParseChrome();
        if (!map.ContainsKey("选区") || !map.ContainsKey("圆角条") || !map.ContainsKey("胶囊"))
        {
            Console.WriteLine("[FAIL] 日志里没有 [chrome] 布局行");
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        var sel = map["选区"];
        var bar = map["圆角条"];
        var pill = map["胶囊"];
        Console.WriteLine($"[geom] 选区={sel.X},{sel.Y},{sel.Width},{sel.Height} " +
                          $"圆角条={bar.X},{bar.Y},{bar.Width},{bar.Height} " +
                          $"胶囊={pill.X},{pill.Y},{pill.Width},{pill.Height}");

        bool barOutside = !bar.IntersectsWith(sel) || bar.Bottom <= sel.Top || bar.Top >= sel.Bottom;
        Console.WriteLine((barOutside ? "[PASS] " : "[FAIL] ") +
                          $"圆角条没有压住选区 条={bar.Y}..{bar.Bottom} 选区={sel.Y}..{sel.Bottom}");
        bool pillOutside = pill.Top >= sel.Bottom || pill.Bottom <= sel.Top;
        Console.WriteLine((pillOutside ? "[PASS] " : "[FAIL] ") + "主胶囊没有压住选区");
        bool noOverlap = !bar.IntersectsWith(pill);
        Console.WriteLine((noOverlap ? "[PASS] " : "[FAIL] ") + "圆角条与主胶囊不重叠");
        bool inScreen = bar.Y >= 0;
        Console.WriteLine((inScreen ? "[PASS] " : "[FAIL] ") + "圆角条完整在屏内");

        // 贴顶截图：上方没有位置时，圆角条退到选区**靠内角的顶部那一行**（贴屏顶），
        // 既不压选区，也不能叠在主胶囊上方。策略应为 corner-right / corner-left（靠内角）。
        string chrome = LastChromeLine() ?? "";
        bool sidePlaced = chrome.Contains("策略=corner-left") || chrome.Contains("策略=corner-right") ||
                          chrome.Contains("策略=left") || chrome.Contains("策略=right");
        Console.WriteLine((sidePlaced ? "[PASS] " : "[FAIL] ") +
                          $"贴顶时圆角条靠选区顶部内角 {chrome}");
        // 需求 3 的第二半句"不能在工具胶囊上方"：指的是两者不能紧贴着叠成两行。
        // 判据取四条边里最宽的那个间距 —— 上下左右任意一侧分开 8px 以上就算分得清。
        int gap = Math.Max(Math.Max(pill.Left - bar.Right, bar.Left - pill.Right),
                           Math.Max(pill.Top - bar.Bottom, bar.Top - pill.Bottom));
        Console.WriteLine((gap >= 8 ? "[PASS] " : "[FAIL] ") +
                          $"圆角条与主胶囊没有叠成两行 最宽间距={gap}px " +
                          $"条={bar.Left}..{bar.Right}/{bar.Top}..{bar.Bottom} " +
                          $"胶囊={pill.Left}..{pill.Right}/{pill.Top}..{pill.Bottom}");

        // 勾一下「阴影」，看一眼勾选态的样式（15px 方框，中心距右边缘 27px）
        int cbX = bar.Right - 27;
        int cbY = bar.Top + bar.Height / 2;
        Click(cbX, cbY);
        Thread.Sleep(400);
        Grab(OutDir + "topbar_2_shadow.png", new Rectangle(bar.X - 10, bar.Y - 10, bar.Width + 40, bar.Height + 40));

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] top-bar");
    }

    /// <summary>最后一条 [chrome] 布局日志行（用于断言圆角条落位策略）。</summary>
    static string? LastChromeLine()
    {
        if (!File.Exists(LogPath)) return null;
        string? last = null;
        foreach (var line in File.ReadAllLines(LogPath))
            if (line.Contains("[chrome]")) last = line;
        return last;
    }

    /// <summary>最后一条 [cursor] 日志行（断言光标形态）。</summary>
    static string? LastCursorLine()
    {
        if (!File.Exists(LogPath)) return null;
        string? last = null;
        foreach (var line in File.ReadAllLines(LogPath))
            if (line.Contains("[cursor]")) last = line;
        return last;
    }

    /// <summary>最后一条含指定关键字的日志行。</summary>
    static string? LastLineWith(string needle)
    {
        if (!File.Exists(LogPath)) return null;
        string? last = null;
        foreach (var line in File.ReadAllLines(LogPath))
            if (line.Contains(needle)) last = line;
        return last;
    }

    /// <summary>
    /// 顶部功能条（参考图二）：顶部居中、可拖动、框选时自动隐藏。
    /// 断言全部基于日志里的几何，再配两张截图给人看。
    /// </summary>
    static void RunQuickBar(Process app, Process note)
    {
        var init = LastLineWith("功能条=");
        Console.WriteLine($"[geom] 初始功能条 {init}");
        Grab(OutDir + "bar_0_visible.png", new Rectangle(1000, 0, 560, 120));

        bool present = init != null;
        Console.WriteLine((present ? "[PASS] " : "[FAIL] ") + "会话开始时功能条存在");

        // 居中判定：|条中心 - 屏幕中心| <= 2px
        var screen = VirtualScreen();
        if (TryRect(init, "功能条", out var b))
        {
            int barCenter = b.Left + b.Width / 2;
            int screenCenter = screen.Left + screen.Width / 2;
            bool centered = Math.Abs(barCenter - screenCenter) <= 2;
            Console.WriteLine((centered ? "[PASS] " : "[FAIL] ") +
                              $"功能条在屏幕顶部居中 条中心={barCenter} 屏中心={screenCenter} 顶={b.Top}");
            bool atTop = b.Top <= screen.Top + 20;
            Console.WriteLine((atTop ? "[PASS] " : "[FAIL] ") + $"功能条贴着屏幕顶部 顶={b.Top}");

            // 按住六点手柄往右下拖 120px
            N.SetCursorPos(b.Left + 18, b.Top + 22);
            Thread.Sleep(250);
            var cursorLine = LastCursorLine() ?? "";
            Console.WriteLine((cursorLine.Contains("bar-drag") ? "[PASS] " : "[FAIL] ") +
                              $"手柄上光标是移动形态 {cursorLine}");

            Drag(b.Left + 18, b.Top + 22, b.Left + 138, b.Top + 92, 12);
            Thread.Sleep(400);
            var moved = LastLineWith("[bar] 拖动结束");
            Console.WriteLine((moved != null ? "[PASS] " : "[FAIL] ") + $"功能条拖动有日志 {(moved ?? "")}");
            Grab(OutDir + "bar_1_dragged.png", new Rectangle(0, 0, 1400, 260));

            // 框选时自动隐藏：按住拖到一半，条原来的位置不该再画那张白卡。
            // 判据用像素而不是日志 —— 条是画进分层位图的，没有窗口/属性可读。
            var barAfterDrag = TryRect(moved, "位置", out var mb) ? mb : b;
            var probe = new Rectangle(barAfterDrag.X, barAfterDrag.Y, barAfterDrag.Width, barAfterDrag.Height);

            int brightBefore = CountBright(GrabTemp(probe));
            Console.WriteLine((brightBefore > 5000 ? "[PASS] " : "[FAIL] ") +
                              $"拖动后功能条还在原位 亮像素={brightBefore}");

            N.SetCursorPos(200, 400);
            Thread.Sleep(140);
            N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(90);
            for (int i = 1; i <= 12; i++) { N.SetCursorPos(200 + 58 * i, 400 + 33 * i); Thread.Sleep(26); }
            Thread.Sleep(200);
            int brightDuring = CountBright(GrabTemp(probe));
            Console.WriteLine((brightDuring < 600 ? "[PASS] " : "[FAIL] ") +
                              $"框选时功能条自动隐藏 亮像素={brightDuring}");
            Grab(OutDir + "bar_2_selecting.png", new Rectangle(0, 0, 1700, 860));

            N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(420);
            int brightAfter = CountBright(GrabTemp(probe));
            // 需求 6：脏区已经框出来了 —— 功能条必须保持隐藏（截图后不可见），
            // 不再是"松手就回来"。
            Console.WriteLine((brightAfter < 600 ? "[PASS] " : "[FAIL] ") +
                              $"框选结束后功能条保持隐藏 亮像素={brightAfter}");
            Grab(OutDir + "bar_3_back.png", new Rectangle(0, 0, 1700, 900));

            // 拖动完成后按 Esc 结束会话
            N.keybd_event(0x1B, 0, 0, IntPtr.Zero);
            N.keybd_event(0x1B, 0, 2, IntPtr.Zero);
            Thread.Sleep(500);
        }
        else
        {
            Console.WriteLine("[FAIL] 日志里没有功能条几何");
        }

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] quickbar");
    }

    /// <summary>
    /// 需求 1：「截图」按钮的下拉菜单（自由 / 窗口 / 全屏），点「全屏截图」直接出整屏图。
    /// </summary>
    static void RunQuickBarMenu(Process app, Process note)
    {
        var init = LastLineWith("功能条=");
        if (!TryRect(init, "截图按钮", out var cap))
        {
            Console.WriteLine("[FAIL] 日志里没有截图按钮几何 " + (init ?? ""));
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        Click(Center(cap));
        Thread.Sleep(400);
        Grab(OutDir + "menu_1_open.png", new Rectangle(cap.Left - 40, cap.Top - 6, 460, 230));

        var open = LastLineWith("[bar] 截图菜单");
        Console.WriteLine((open != null && open.Contains("展开") ? "[PASS] " : "[FAIL] ") +
                          $"点「截图」展开下拉菜单 {(open ?? "")}");

        // 菜单卡片：宽 172、三项各 36 高，贴在条下方 8px，左边缘与「截图」按钮对齐
        var bar = new Rectangle(cap.Left, cap.Top, 0, 0);
        if (TryRect(init, "功能条", out var bb)) bar = bb;
        int menuLeft = cap.Left;
        int menuTop = bar.Bottom + 8;
        var probe = new Rectangle(menuLeft + 4, menuTop + 4, 160, 100);
        int bright = CountBright(GrabTemp(probe));
        Console.WriteLine((bright > 4000 ? "[PASS] " : "[FAIL] ") +
                          $"菜单卡片画出来了 方块={probe} 亮像素={bright}");

        // 第三项「全屏截图」：菜单内边距 6 + 2*36 + 18
        Click(menuLeft + 60, menuTop + 6 + 2 * 36 + 18);
        Thread.Sleep(1200);
        var picked = LastLineWith("[bar] 菜单选择");
        Console.WriteLine((picked != null && picked.Contains("全屏截图") ? "[PASS] " : "[FAIL] ") +
                          $"点菜单项「全屏截图」 {(picked ?? "")}");

        var ended = LastLineWith("会话结束");
        Console.WriteLine((ended != null ? "[PASS] " : "[FAIL] ") + $"全屏截图结束会话 {(ended ?? "")}");
        Grab(OutDir + "menu_2_after.png", new Rectangle(0, 0, 900, 260));

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] barmenu");
    }

    /// <summary>
    /// 需求 2：圆圈是选择框（文字不是），点它就隐藏功能条自己；小柄可再展开。
    /// </summary>
    static void RunQuickBarCollapse(Process app, Process note)
    {
        var init = LastLineWith("功能条=");
        if (!TryRect(init, "圆圈", out var chk) || !TryRect(init, "功能条", out var bar))
        {
            Console.WriteLine("[FAIL] 日志里没有圆圈/功能条几何 " + (init ?? ""));
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        // 点文字（圆圈右侧 40px）不该收起 —— 只有圆圈是选择框
        Click(chk.Right + 40, chk.Top + chk.Height / 2);
        Thread.Sleep(350);
        var afterText = LastLineWith("[bar] 选择框勾选");
        Console.WriteLine((afterText == null ? "[PASS] " : "[FAIL] ") +
                          $"点「隐藏当前窗口」文字不会收起功能条 {(afterText ?? "（没有收起日志）")}");

        Click(Center(chk));
        Thread.Sleep(420);
        Grab(OutDir + "collapse_1.png", new Rectangle(bar.Left - 20, bar.Top - 8, 460, 100));
        var collapsed = LastLineWith("[bar] 选择框勾选");
        Console.WriteLine((collapsed != null ? "[PASS] " : "[FAIL] ") +
                          $"点圆圈收起功能条 {(collapsed ?? "")}");

        // 条右半边应该已经空了（只剩左边那个小柄）
        var probe = new Rectangle(bar.Left + 120, bar.Top + 6, 200, 34);
        int bright = CountBright(GrabTemp(probe));
        Console.WriteLine((bright < 800 ? "[PASS] " : "[FAIL] ") +
                          $"收起后条不再绘制 亮像素={bright}");

        // 小柄在条左端 34×22
        Click(bar.Left + 18, bar.Top + bar.Height / 2);
        Thread.Sleep(420);
        var expanded = LastLineWith("[bar] 展开");
        Console.WriteLine((expanded != null ? "[PASS] " : "[FAIL] ") +
                          $"点小柄展开功能条 {(expanded ?? "")}");
        int bright2 = CountBright(GrabTemp(probe));
        Console.WriteLine((bright2 > 3000 ? "[PASS] " : "[FAIL] ") +
                          $"展开后条回来了 亮像素={bright2}");

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] barcollapse");
    }

    /// <summary>需求 3：截图界面里在任意地方点右键都能退出（这里专门点在顶部功能条上）。</summary>
    static void RunRightCancel(Process app, Process note)
    {
        var init = LastLineWith("功能条=");
        Point at = TryRect(init, "功能条", out var bar)
            ? new Point(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2)
            : new Point(640, 760);

        RightClick(at.X, at.Y);
        Thread.Sleep(700);
        Grab(OutDir + "rcancel_1.png", new Rectangle(0, 0, 900, 320));

        var line = LastLineWith("[input] 右键退出截图");
        Console.WriteLine((line != null ? "[PASS] " : "[FAIL] ") +
                          $"右键退出有日志 {(line ?? "（日志里没有）")}");
        var ended = LastLineWith("会话结束");
        Console.WriteLine((ended != null ? "[PASS] " : "[FAIL] ") + $"右键结束了会话 {(ended ?? "")}");

        // iTu 本身必须还活着（退出的是截图界面，不是进程）
        bool alive = false;
        try { alive = !app.HasExited; } catch { }
        Console.WriteLine((alive ? "[PASS] " : "[FAIL] ") + "进程仍在托盘常驻");

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] rcancel");
    }

    /// <summary>需求 8：截左下角时圆角条不能跑到屏幕左上角。</summary>
    static void RunBottomBar(Process app, Process note)
    {
        var screen = VirtualScreen();
        int l = screen.Left + 40, t = screen.Bottom - 360;
        int r = screen.Left + 900, b = screen.Bottom - 20;
        Drag(l, t, r, b);
        Thread.Sleep(700);
        Grab(OutDir + "bottom_1.png", new Rectangle(screen.Left, Math.Max(0, screen.Bottom - 620), 1200, 620));

        var map = ParseChrome();
        if (!map.ContainsKey("选区") || !map.ContainsKey("圆角条") || !map.ContainsKey("胶囊"))
        {
            Console.WriteLine("[FAIL] 日志里没有 [chrome] 布局行");
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            return;
        }

        var sel = map["选区"];
        var bar = map["圆角条"];
        var pill = map["胶囊"];
        Console.WriteLine($"[geom] 选区={sel} 圆角条={bar} 胶囊={pill}");

        bool outside = !bar.IntersectsWith(sel);
        Console.WriteLine((outside ? "[PASS] " : "[FAIL] ") + "左下角选区：圆角条没压住选区");

        bool notTopLeft = !(bar.Left <= screen.Left + 12 && bar.Top <= screen.Top + 24);
        Console.WriteLine((notTopLeft ? "[PASS] " : "[FAIL] ") +
                          $"圆角条没有跑到屏幕左上角 条={bar.Left},{bar.Top}");

        bool nearSel = bar.Top > sel.Top - 260 || bar.Bottom < sel.Bottom;
        Console.WriteLine((nearSel ? "[PASS] " : "[FAIL] ") +
                          $"圆角条贴着选区而不是飘到别处 条Y={bar.Top} 选区Y={sel.Top}");

        var chrome = LastChromeLine() ?? "";
        bool noClamp = !chrome.Contains("策略=clamp");
        Console.WriteLine((noClamp ? "[PASS] " : "[FAIL] ") + $"不是兜底夹取 {chrome}");

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] bottombar");
    }

    /// <summary>
    /// 小选区：截图框缩到 140×70 以下时，编辑工具不能消失（用户报的 1、2）。
    /// 断言 [chrome] 行里胶囊仍在、尺寸仍是固定 595×38、且不压选区。
    /// </summary>
    static void RunSmallSelection(Process app, Process note)
    {
        var screen = VirtualScreen();
        int l = screen.Left + 700, t = screen.Top + 400;

        // 宽 120 × 高 60：140 与 70 两个门槛同时踩中
        Drag(l, t, l + 120, t + 60);
        Thread.Sleep(700);
        Grab(OutDir + "small_1_120x60.png",
             new Rectangle(Math.Max(0, l - 220), Math.Max(0, t - 240), 900, 520));

        var line1 = LastChromeLineSafe();
        var m1 = ParseChromeText(line1);
        bool has1 = m1.ContainsKey("胶囊") && m1["胶囊"].Width > 0;
        Console.WriteLine((has1 ? "[PASS] " : "[FAIL] ") +
                          $"120×60 选区仍有编辑工具 {(line1 ?? "（日志里没有 [chrome] 行）")}");

        if (has1)
        {
            var pill = m1["胶囊"];
            bool fixedSize = pill.Width == 595 && pill.Height == 38;
            Console.WriteLine((fixedSize ? "[PASS] " : "[FAIL] ") +
                              $"胶囊尺寸仍是 595×38 实际={pill.Width}×{pill.Height}");

            bool noCover = !m1.TryGetValue("选区", out var s1) || !pill.IntersectsWith(s1);
            Console.WriteLine((noCover ? "[PASS] " : "[FAIL] ") + "胶囊没有压住这个小小的选区");
        }

        // 再缩到 60×40 —— 旧代码在这个尺寸连圆角条的位置都不算
        Click(screen.Left + 1300, screen.Top + 700);            // 先点空处清掉上一个选区
        Thread.Sleep(300);
        Drag(l, t, l + 60, t + 40);
        Thread.Sleep(700);
        Grab(OutDir + "small_2_60x40.png",
             new Rectangle(Math.Max(0, l - 220), Math.Max(0, t - 240), 900, 520));

        var line2 = LastChromeLineSafe();
        var m2 = ParseChromeText(line2);
        bool has2 = m2.ContainsKey("胶囊") && m2["胶囊"].Width > 0;
        Console.WriteLine((has2 ? "[PASS] " : "[FAIL] ") +
                          $"60×40 选区仍有编辑工具 {(line2 ?? "（日志里没有 [chrome] 行）")}");

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] small");
    }

    /// <summary>
    /// 圆角条锚定：选区贴左上/右上（水平贴边）时，圆角条要锚在**选区顶部**、靠内角 ——
    /// 脏区靠左 → 条在脏区右上；脏区靠右 → 条在脏区左上。
    /// 做法是同一角、同一高度拖两次（窄 500 / 宽 760），比两次的 策略 与 条的位置，
    /// 确认宽度跨过 590 不会让条换行、换边。
    ///
    /// 选区顶边留出足够空间（不贴屏幕顶），这样"锚在顶部"才有意义；
    /// 真贴屏幕顶时顶部塞不下，自然退到贴屏顶 + 靠内角，那是合理的退化，不在这里断言。
    /// </summary>
    static void RunCornerStability(Process app, Process note)
    {
        var screen = VirtualScreen();
        int t = screen.Top + 160, h = 220;   // 顶边距屏顶 160px，顶部放得下 34px 高的条 + 间距

        (Dictionary<string, Rectangle> Map, string Strategy) Dragged(int x1, int y1, int w)
        {
            Click(screen.Left + (screen.Width / 2), screen.Bottom - 80);   // 清掉上一个选区
            Thread.Sleep(300);
            Drag(x1, y1, x1 + w, y1 + h);
            Thread.Sleep(700);
            var line = LastChromeLineSafe();
            return (ParseChromeText(line), StrategyOf(line));
        }

        // ---- 左上角：贴左边（顶边有空间）----
        // 脏区靠左 → 圆角条应锚在脏区右上（top-right）
        var tlNarrow = Dragged(screen.Left + 2, t, 500);
        var tlWide = Dragged(screen.Left + 2, t, 760);

        CheckCorner("左上", "top-right", tlNarrow, tlWide, OutDir + "corner_tl.png");

        // ---- 右上角：贴右边（顶边有空间）----
        // 脏区靠右 → 圆角条应锚在脏区左上（top-left）
        var trNarrow = Dragged(screen.Right - 2 - 500, t, 500);
        var trWide = Dragged(screen.Right - 2 - 760, t, 760);

        CheckCorner("右上", "top-left", trNarrow, trWide, OutDir + "corner_tr.png");

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] cornerbar");
    }

    /// <summary>对比贴角选区在 500 → 760 两个宽度下的圆角条位置。</summary>
    static void CheckCorner(string corner, string side,
                            (Dictionary<string, Rectangle> Map, string Strategy) narrow,
                            (Dictionary<string, Rectangle> Map, string Strategy) wide,
                            string shot)
    {
        if (!narrow.Map.TryGetValue("圆角条", out var b1) || !wide.Map.TryGetValue("圆角条", out var b2))
        {
            Console.WriteLine($"[FAIL] {corner}：日志里没有圆角条矩形 (窄={narrow.Strategy} 宽={wide.Strategy})");
            return;
        }

        Grab(shot, new Rectangle(Math.Max(0, Math.Min(b1.Left, b2.Left) - 40),
                                 Math.Max(0, Math.Min(b1.Top, b2.Top) - 60), 1200, 420));

        bool sameRow = Math.Abs(b1.Top - b2.Top) <= 2;
        Console.WriteLine((sameRow ? "[PASS] " : "[FAIL] ") +
                          $"{corner}：宽度 500→760 圆角条没换行 窄Y={b1.Top} 宽Y={b2.Top}");

        bool sameSide = narrow.Strategy.StartsWith(side, StringComparison.Ordinal) &&
                        wide.Strategy.StartsWith(side, StringComparison.Ordinal);
        Console.WriteLine((sameSide ? "[PASS] " : "[FAIL] ") +
                          $"{corner}：策略稳定在选区{side}侧 窄={narrow.Strategy} 宽={wide.Strategy}");

        bool noPill = !narrow.Map.TryGetValue("胶囊", out var p1) || !b1.IntersectsWith(p1);
        bool noPill2 = !wide.Map.TryGetValue("胶囊", out var p2) || !b2.IntersectsWith(p2);
        Console.WriteLine((noPill && noPill2 ? "[PASS] " : "[FAIL] ") + $"{corner}：圆角条没压住胶囊");

        bool noSel = !narrow.Map.TryGetValue("选区", out var s1) || !b1.IntersectsWith(s1);
        bool noSel2 = !wide.Map.TryGetValue("选区", out var s2) || !b2.IntersectsWith(s2);
        Console.WriteLine((noSel && noSel2 ? "[PASS] " : "[FAIL] ") + $"{corner}：圆角条没压住选区");

        // 关键：圆角条要锚在选区**顶部**那一条边上（贴顶、留 10px 间距），
        // 而不是跟胶囊竖直居中对齐（胶囊在选区下方，与顶边差一大截）。
        bool nearTop = narrow.Map.TryGetValue("选区", out var st1) &&
                       Math.Abs(b1.Bottom - (st1.Top - 10)) <= 2;
        bool nearTop2 = wide.Map.TryGetValue("选区", out var st2) &&
                        Math.Abs(b2.Bottom - (st2.Top - 10)) <= 2;
        Console.WriteLine((nearTop && nearTop2 ? "[PASS] " : "[FAIL] ") +
                          $"{corner}：圆角条贴着选区顶部 窄条底={b1.Bottom} 选区顶={st1.Top} / 宽条底={b2.Bottom} 选区顶={st2.Top}");

        // 贴顶、且圆角条靠选区内侧边角：左角时条右缘对齐选区右缘，右角时条左缘对齐选区左缘。
        if (corner == "左上")
        {
            bool rightAligned = false;
            if (narrow.Map.TryGetValue("选区", out var sr1) && wide.Map.TryGetValue("选区", out var sr2))
                rightAligned = Math.Abs(b1.Right - sr1.Right) <= 2 && Math.Abs(b2.Right - sr2.Right) <= 2;
            Console.WriteLine((rightAligned ? "[PASS] " : "[FAIL] ") +
                              $"{corner}：条右缘对齐选区右缘（脏区右上）窄右={b1.Right} 宽右={b2.Right}");
        }
        else
        {
            bool leftAligned = false;
            if (narrow.Map.TryGetValue("选区", out var sl1) && wide.Map.TryGetValue("选区", out var sl2))
                leftAligned = Math.Abs(b1.Left - sl1.Left) <= 2 && Math.Abs(b2.Left - sl2.Left) <= 2;
            Console.WriteLine((leftAligned ? "[PASS] " : "[FAIL] ") +
                              $"{corner}：条左缘对齐选区左缘（脏区左上）窄左={b1.Left} 宽左={b2.Left}");
        }
    }

    /// <summary>
    /// 自动识别窗口：悬停高亮、单击成脏区、脏区里是移动光标、移动脏区时控件跟随。
    /// </summary>
    static void RunWindowPick(Process app, Process note)
    {
        Grab(OutDir + "win_0_bar.png", new Rectangle(0, 0, 1200, 140));

        // 悬停在记事本窗口上
        N.SetCursorPos(300, 300);
        Thread.Sleep(500);
        Grab(OutDir + "win_1_hover.png", new Rectangle(0, 0, 1000, 620));

        // 需求 4：移上去就整窗高亮，日志里要能读出"高亮的是哪个窗口、哪块矩形"
        var hover = LastLineWith("[hover] 窗口");
        Console.WriteLine((hover != null ? "[PASS] " : "[FAIL] ") +
                          $"鼠标移到窗口上整窗高亮 {(hover ?? "（日志里没有 [hover] 行）")}");

        // 需求 4 的后半句：标签里除了窗口标题，还要有程序名和窗口尺寸
        bool hasProc = hover != null && hover.ToLowerInvariant().Contains("notepad");
        bool hasSize = hover != null && hover.Contains("×");
        Console.WriteLine((hasProc && hasSize ? "[PASS] " : "[FAIL] ") +
                          $"悬停标签带程序名与窗口尺寸 {(hover ?? "")}");

        // 单击 → 整个窗口成为脏区
        Click(300, 300);
        Thread.Sleep(600);
        Grab(OutDir + "win_2_picked.png", new Rectangle(0, 0, 1000, 700));

        var picked = LastLineWith("[window] 单击选中窗口");
        Console.WriteLine((picked != null ? "[PASS] " : "[FAIL] ") +
                          $"单击窗口成为脏区 {(picked ?? "（日志里没有 [window] 行）")}");

        var chromeText = LastChromeLine() ?? "";
        var afterPick = ParseChrome();
        bool hasPill = afterPick.ContainsKey("胶囊");
        Console.WriteLine((hasPill ? "[PASS] " : "[FAIL] ") + "点选窗口后工具胶囊出现 " + chromeText);

        // 脏区里应当是"移动"光标
        N.SetCursorPos(450, 300);
        Thread.Sleep(400);
        var moveCursor = LastCursorLine() ?? "";
        Console.WriteLine((moveCursor.Contains("move") ? "[PASS] " : "[FAIL] ") +
                          $"脏区里是移动光标 {moveCursor}");

        // 拖动整块脏区：控件必须跟着走
        int pillBefore = afterPick.TryGetValue("胶囊", out var p0) ? p0.X : 0;
        int barBefore = afterPick.TryGetValue("圆角条", out var b0) ? b0.Y : 0;
        Drag(450, 300, 530, 380, 14);
        Thread.Sleep(500);
        Grab(OutDir + "win_3_moved.png", new Rectangle(0, 0, 1100, 760));

        var moved = ParseChrome();
        var selAfter = moved.TryGetValue("选区", out var s1) ? s1 : Rectangle.Empty;
        var selBefore = afterPick.TryGetValue("选区", out var s0) ? s0 : Rectangle.Empty;
        bool selMoved = selAfter.X == selBefore.X + 80 && selAfter.Y == selBefore.Y + 80;
        Console.WriteLine((selMoved ? "[PASS] " : "[FAIL] ") +
                          $"脏区被整体移动 前={selBefore} 后={selAfter}");

        int pillAfter = moved.TryGetValue("胶囊", out var p1) ? p1.X : 0;
        bool pillFollow = Math.Abs(pillAfter - pillBefore) == 80;
        Console.WriteLine((pillFollow ? "[PASS] " : "[FAIL] ") +
                          $"胶囊跟着脏区移动 前={pillBefore} 后={pillAfter}");

        int barAfter = moved.TryGetValue("圆角条", out var b1) ? b1.Y : 0;
        bool barFollow = barAfter != barBefore;
        Console.WriteLine((barFollow ? "[PASS] " : "[FAIL] ") +
                          $"圆角条跟着脏区移动 前Y={barBefore} 后Y={barAfter}");

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] windows");
    }

    /// <summary>
    /// 诊断：窄选区（< 600 宽）整体移动时，圆角条是否紧跟选区平移（用户报"低于 600 停止跟随"）。
    /// 拖 500 宽选区 → 整体平移 (+80,+80) → 比移动前后选区与圆角条的位移差。
    /// </summary>
    static void RunBarMove(Process app, Process note)
    {
        var screen = VirtualScreen();
        // 选区放在屏幕中部偏上，顶边留足空间让圆角条能锚在顶部
        int l = screen.Left + 200, t = screen.Top + 200, w = 500, h = 180;
        Drag(l, t, l + w, t + h);
        Thread.Sleep(700);
        var before = ParseChrome();

        // 整体移动：起点在选区内部（避免点到控制点/圆角条）
        int cx = l + w / 2, cy = t + h / 2;
        Drag(cx, cy, cx + 80, cy + 80, 14);
        Thread.Sleep(700);
        var after = ParseChrome();

        if (!before.TryGetValue("选区", out var s0) || !after.TryGetValue("选区", out var s1) ||
            !before.TryGetValue("圆角条", out var b0) || !after.TryGetValue("圆角条", out var b1))
        {
            Console.WriteLine("[FAIL] 日志里缺选区或圆角条矩形");
            try { app.Kill(); } catch { }
            try { note.Kill(); } catch { }
            Console.WriteLine("[OK] barmove");
            return;
        }

        int selDx = s1.X - s0.X, selDy = s1.Y - s0.Y;
        int barDx = b1.X - b0.X, barDy = b1.Y - b0.Y;
        Console.WriteLine($"[geom] 选区位移=({selDx},{selDy}) 圆角条位移=({barDx},{barDy}) 条前={b0} 条后={b1}");

        bool selMoved = selDx == 80 && selDy == 80;
        Console.WriteLine((selMoved ? "[PASS] " : "[FAIL] ") + $"选区整体移动了 (+80,+80) 实际=({selDx},{selDy})");

        bool barFollow = barDx == selDx && barDy == selDy;
        Console.WriteLine((barFollow ? "[PASS] " : "[FAIL] ") +
                          $"圆角条紧跟选区平移 条位移=({barDx},{barDy}) 选区位移=({selDx},{selDy})");

        // 平移后圆角条仍应贴着选区顶部（相对位置不变）
        bool stillTop = Math.Abs((b1.Bottom - s1.Top) - (b0.Bottom - s0.Top)) <= 1;
        Console.WriteLine((stillTop ? "[PASS] " : "[FAIL] ") +
                          $"平移后圆角条仍贴着选区顶部 前间距={b0.Bottom - s0.Top} 后间距={b1.Bottom - s1.Top}");

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] barmove");
    }

    /// <summary>
    /// 诊断：脏区在左上/右上角时，拖右/左边缘控制点缩放宽度（跨过 600），圆角条是否实时紧跟选区。
    /// 用户报：脏区低于 600 圆角条就停止位移。根因是 PlaceRadiusBar 的 right/leftEdge 用
    /// max(sel.Right, pill.Right)，选区窄于胶囊（595）时锚点跳到胶囊外缘，条脱离选区。
    /// </summary>
    static void RunBarResize(Process app, Process note)
    {
        var screen = VirtualScreen();
        int t = screen.Top + 160, h = 220;   // 顶边留空间，让圆角条能锚在顶部

        // ---- 左上角：贴左边，先拖 760 宽，再拖右边缘缩到 500（跨过 600）----
        int l = screen.Left + 2;
        Drag(l, t, l + 760, t + h);
        Thread.Sleep(700);
        var wide = ParseChrome();
        Grab(OutDir + "resize_tl_wide.png", new Rectangle(l - 40, t - 60, 900, 360));

        // 拖右边缘控制点：从 (l+760, t+h/2) 拖到 (l+500, t+h/2)，宽度 760 → 500
        Drag(l + 760, t + h / 2, l + 500, t + h / 2, 10);
        Thread.Sleep(700);
        var narrow = ParseChrome();
        Grab(OutDir + "resize_tl_narrow.png", new Rectangle(l - 40, t - 60, 900, 360));

        CheckResize("左上", wide, narrow, true);

        // ---- 右上角：贴右边，先拖 760 宽，再拖左边缘缩到 500 ----
        int r = screen.Right - 2;
        Drag(r - 760, t, r, t + h);
        Thread.Sleep(700);
        var trWide = ParseChrome();
        Grab(OutDir + "resize_tr_wide.png", new Rectangle(r - 860, t - 60, 900, 360));

        // 拖左边缘控制点：从 (r-760, t+h/2) 拖到 (r-500, t+h/2)，宽度 760 → 500
        Drag(r - 760, t + h / 2, r - 500, t + h / 2, 10);
        Thread.Sleep(700);
        var trNarrow = ParseChrome();
        Grab(OutDir + "resize_tr_narrow.png", new Rectangle(r - 860, t - 60, 900, 360));

        CheckResize("右上", trWide, trNarrow, false);

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] barresize");
    }

    /// <summary>缩放跨 600 前后，圆角条是否始终贴着选区顶、对齐选区内侧边。</summary>
    static void CheckResize(string corner,
                            Dictionary<string, Rectangle> wide,
                            Dictionary<string, Rectangle> narrow,
                            bool selOnLeft)
    {
        bool ok = true;

        if (!wide.TryGetValue("选区", out var sw) || !narrow.TryGetValue("选区", out var sn))
        { Console.WriteLine($"[FAIL] {corner}：日志缺选区"); return; }
        if (!wide.TryGetValue("圆角条", out var bw) || !narrow.TryGetValue("圆角条", out var bn))
        { Console.WriteLine($"[FAIL] {corner}：日志缺圆角条"); return; }

        // 选区宽度确实跨过了 600（760 → 500）
        bool crossed = sw.Width >= 600 && sn.Width < 600;
        Console.WriteLine((crossed ? "[PASS] " : "[FAIL] ") +
                          $"{corner}：选区宽度跨过 600 宽={sw.Width} → 窄={sn.Width}");

        // 圆角条在窄选区下仍贴着选区顶部（条底 = 选区顶 - 10，容差 2）
        bool nearTopW = Math.Abs(bw.Bottom - (sw.Top - 10)) <= 2;
        bool nearTopN = Math.Abs(bn.Bottom - (sn.Top - 10)) <= 2;
        ok &= nearTopW && nearTopN;
        Console.WriteLine(((nearTopW && nearTopN) ? "[PASS] " : "[FAIL] ") +
                          $"{corner}：圆角条始终贴着选区顶部 宽条底={bw.Bottom}/选区顶={sw.Top} 窄条底={bn.Bottom}/选区顶={sn.Top}");

        // 圆角条对齐选区的内边（靠左选区对齐右缘，靠右选区对齐左缘），缩放前后都对齐
        bool aligned;
        if (selOnLeft)
            aligned = Math.Abs(bw.Right - sw.Right) <= 2 && Math.Abs(bn.Right - sn.Right) <= 2;
        else
            aligned = Math.Abs(bw.Left - sw.Left) <= 2 && Math.Abs(bn.Left - sn.Left) <= 2;
        ok &= aligned;
        Console.WriteLine((aligned ? "[PASS] " : "[FAIL] ") +
                          $"{corner}：圆角条对齐选区内侧边（缩放前后） 宽条={bw} 窄条={bn}");

        // 圆角条不压选区、不压胶囊
        bool noSel = !bw.IntersectsWith(sw) && !bn.IntersectsWith(sn);
        bool noPill = (!wide.TryGetValue("胶囊", out var pw) || !bw.IntersectsWith(pw)) &&
                      (!narrow.TryGetValue("胶囊", out var pn) || !bn.IntersectsWith(pn));
        ok &= noSel && noPill;
        Console.WriteLine(((noSel && noPill) ? "[PASS] " : "[FAIL] ") +
                          $"{corner}：圆角条不压选区也不压胶囊 压选区={!noSel} 压胶囊={!noPill}");
    }

    /// <summary>解析最后一条 [chrome] 布局日志。</summary>
    static Dictionary<string, Rectangle> ParseChrome()
    {
        if (!File.Exists(LogPath)) return new Dictionary<string, Rectangle>();

        string? last = null;
        foreach (var line in File.ReadAllLines(LogPath))
            if (line.Contains("[chrome]")) last = line;

        return ParseChromeText(last);
    }

    /// <summary>解析某一条 [chrome] 文本（用于"拖一次就取一次布局"，而不是永远取最后一条）。</summary>
    static Dictionary<string, Rectangle> ParseChromeText(string? line)
    {
        var map = new Dictionary<string, Rectangle>();
        if (line == null) return map;
        foreach (var name in new[] { "选区", "胶囊", "圆角条" })
            if (TryRect(line, name, out var r)) map[name] = r;
        return map;
    }

    /// <summary>取 [chrome] 行里的 策略=xxx（没有则空串）。</summary>
    static string StrategyOf(string? line)
    {
        if (line == null) return "";
        int i = line.IndexOf("策略=", StringComparison.Ordinal);
        return i < 0 ? "" : line[(i + 3)..].Trim();
    }

    /// <summary>日志里最后一条 [chrome] 行。</summary>
    static string? LastChromeLineSafe()
    {
        if (!File.Exists(LogPath)) return null;
        string? last = null;
        foreach (var line in File.ReadAllLines(LogPath))
            if (line.Contains("[chrome]")) last = line;
        return last;
    }

    /// <summary>该进程当前可见的、有实际尺寸的顶层窗口数（浮层算 1 个）。</summary>
    static int VisibleAppWindows(int pid)
    {
        int n = 0;
        N.EnumWindows((h, _) =>
        {
            N.GetWindowThreadProcessId(h, out uint owner);
            if (owner != (uint)pid) return true;
            if (!N.IsWindowVisible(h)) return true;
            N.GetWindowRect(h, out var r);
            if (r.Right - r.Left > 40 && r.Bottom - r.Top > 40) n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }

    /// <summary>深色像素数（判断"文字还在不在"）。</summary>
    static int CountDark(string path)
    {
        using var bmp = new Bitmap(path);
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if ((c.R + c.G + c.B) / 3 < 110) n++;
            }
        return n;
    }

    /// <summary>
    /// 翻译要"直接改写截图"：点翻译后，译文必须画进选区画面（原文被填掉），
    /// 既不弹对话框，也不结束会话。
    /// </summary>
    static void RunTranslateApply(Process app, Process note, int pillX, int pillY, float s)
    {
        Grab(OutDir + "trans_0_before.png", new Rectangle(SelL, SelT, SelR - SelL, SelB - SelT));

        Click(pillX + (int)Math.Round(332 * s), pillY + (int)(19 * s));   // 翻译

        // 等结果出来就立刻抓图：格位上的「已翻译 N 行」只停留 3 秒左右
        bool done = WaitForLog("已把", 20000) || WaitForLog("翻译失败", 1000);
        Thread.Sleep(300);
        Grab(OutDir + "trans_1_after.png", new Rectangle(SelL, SelT, SelR - SelL, SelB - SelT));
        Grab(OutDir + "trans_2_pill_label.png", new Rectangle(0, 150, 1100, 220));

        var lines = File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>();
        int applied = 0;
        var m = System.Text.RegularExpressions.Regex.Match(
            string.Join("\n", lines), @"已把 (\d+) 行译文写回选区画面");
        if (m.Success) applied = int.Parse(m.Groups[1].Value);

        Console.WriteLine((done ? "[PASS] " : "[FAIL] ") + "翻译任务有结果（不是静默无响应）");
        Console.WriteLine((applied > 0 ? "[PASS] " : "[FAIL] ") + $"译文已写回画面（{applied} 行）");
        Console.WriteLine((!lines.Any(l => l.Contains("会话结束")) ? "[PASS] " : "[FAIL] ") +
                          "翻译没有结束截图会话（图上直接看到译文）");

        Console.WriteLine("[log]");
        foreach (var line in lines)
            if (line.Contains("[text]") || line.Contains("[tool]") || line.Contains("会话结束"))
                Console.WriteLine("  " + line);

        // 最后一步：保存，确认落盘的那张图**就是**翻译后的画面（不是原文）
        var before = NewestSavedFile(SaveDir());
        Click(pillX + (int)Math.Round(501 * s), pillY + (int)(19 * s));   // 保存
        Thread.Sleep(1500);
        var after = NewestSavedFile(SaveDir());
        bool saved = after != null && after != before;
        Console.WriteLine((saved ? "[PASS] " : "[FAIL] ") + $"保存出图 {after ?? "（没有新文件）"}");
        if (saved) File.Copy(after!, OutDir + "trans_3_saved.png", true);

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] translate-apply");
    }

    /// <summary>保存目录里最新的图片文件（用来证明"真的落盘了一张新图"）。</summary>
    static string? NewestSavedFile(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "*.png")
                .Concat(Directory.GetFiles(dir, "*.jpg"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static string SaveDir()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "iTu", "settings.json");
            if (File.Exists(p))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("SaveDirectory", out var v))
                {
                    var d = v.GetString();
                    if (!string.IsNullOrWhiteSpace(d)) return d!;
                }
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iTu");
    }

    static HashSet<string> Snapshot(string dir) => Directory.Exists(dir)
        ? new HashSet<string>(Directory.GetFiles(dir), StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static IntPtr WaitWindow(string title, int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            var h = N.FindWindow(null, title);
            if (h != IntPtr.Zero) return h;
            Thread.Sleep(100);
        }
        return IntPtr.Zero;
    }

    static bool WaitGone(string title, int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            if (N.FindWindow(null, title) == IntPtr.Zero) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    static Dictionary<string, Rectangle> ParseGeometry()
    {
        var map = new Dictionary<string, Rectangle>();
        var log = Path.Combine(Path.GetTempPath(), "itu.log");
        if (!File.Exists(log)) return map;

        foreach (var line in File.ReadAllLines(log))
        {
            if (line.Contains("[record] 面板"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"rect=(-?\d+),(-?\d+),(\d+),(\d+)");
                if (m.Success) map["panel"] = Rect(m);
            }
            else if (line.Contains("[record] 按钮"))
            {
                foreach (var name in new[] { "暂停", "停止", "取消" })
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, name + @"=(-?\d+),(-?\d+),(\d+),(\d+)");
                    if (m.Success) map[name] = Rect(m);
                }
            }
        }
        return map;
    }

    static Rectangle Rect(System.Text.RegularExpressions.Match m) => new(
        int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
        int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));

    /// <summary>
    /// 从一行日志里取某个 key 的矩形，两种写法都认：
    /// 　"功能条={X=1075,Y=10,Width=409,Height=46}"（Rectangle.ToString）
    /// 　"选区=60,48,700,140"（手写四元组）
    /// </summary>
    static bool TryRect(string? line, string key, out Rectangle r)
    {
        r = Rectangle.Empty;
        if (string.IsNullOrEmpty(line)) return false;
        var m = System.Text.RegularExpressions.Regex.Match(
            line,
            System.Text.RegularExpressions.Regex.Escape(key) +
            @"=(?:(-?\d+),(-?\d+),(\d+),(\d+)|\{X=(-?\d+),Y=(-?\d+),Width=(\d+),Height=(\d+)\})");
        if (!m.Success) return false;
        int g = m.Groups[1].Success ? 1 : 5;
        r = new Rectangle(int.Parse(m.Groups[g].Value), int.Parse(m.Groups[g + 1].Value),
                          int.Parse(m.Groups[g + 2].Value), int.Parse(m.Groups[g + 3].Value));
        return true;
    }

    static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    static Rectangle Padded(Rectangle r, int n) => Rectangle.FromLTRB(
        Math.Max(0, r.Left - n), Math.Max(0, r.Top - n), r.Right + n, r.Bottom + n);

    static int DiffPixels(string a, string b)
    {
        using var ia = new Bitmap(a);
        using var ib = new Bitmap(b);
        if (ia.Width != ib.Width || ia.Height != ib.Height) return int.MaxValue;

        int n = 0;
        for (int y = 0; y < ia.Height; y++)
            for (int x = 0; x < ia.Width; x++)
                if (ia.GetPixel(x, y).ToArgb() != ib.GetPixel(x, y).ToArgb()) n++;
        return n;
    }

    static string? NewFile(HashSet<string> before, string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, pattern)
            .Where(f => !before.Contains(f))
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .FirstOrDefault();
    }

    static void VerifyOutput(string path)
    {
        long bytes = new FileInfo(path).Length;
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            using var img = Image.FromFile(path);
            var dim = new FrameDimension(img.FrameDimensionsList[0]);
            int frames = img.GetFrameCount(dim);
            Console.WriteLine($"[check] {Path.GetFileName(path)} {img.Width}x{img.Height} 帧数={frames} 大小={bytes}B");
            Console.WriteLine(frames >= 5 && bytes > 1000 ? "[PASS] 输出为多帧动画" : "[FAIL] 帧数/大小异常");
            return;
        }
        Console.WriteLine($"[check] {Path.GetFileName(path)} 大小={bytes}B");
        Console.WriteLine(bytes > 1000 ? "[PASS] 输出文件非空" : "[FAIL] 输出文件异常");
    }

    /// <summary>
    /// 走"录制热键 → 拖框 → 回车确认"的正式路径。
    ///
    /// 不能用 WM_ACTIVATE_APP：那个消息固定唤起**截图**流程（截图会直接落盘一张 png），
    /// 录制必须由录制热键（第 2 个注册的 id）触发。
    /// </summary>
    static void StartRecordCapture()
    {
        var sink = N.FindWindow(null, "iTuHotkeySink");
        N.PostMessage(sink, 0x0312, (IntPtr)2, IntPtr.Zero);
        Thread.Sleep(1300);

        Drag(RecSel[0], RecSel[1], RecSel[2], RecSel[3]);

        // 选区要靠 Enter / 确认格位才会结束，这一步录制和截图是一样的
        N.keybd_event(0x0D, 0, 0, IntPtr.Zero);
        Thread.Sleep(60);
        N.keybd_event(0x0D, 0, 2, IntPtr.Zero);
        Thread.Sleep(600);
    }

    static void RunRecord(string exe)
    {
        var sample = Root + "verify/record_sample.txt";
        File.WriteAllText(sample,
            "iTu 录制面板验收" + Environment.NewLine +
            "RECORD PANEL TEST" + Environment.NewLine +
            "1234567890" + Environment.NewLine,
            new UTF8Encoding(true));

        var note = Process.Start(new ProcessStartInfo("notepad.exe", "\"" + sample + "\""))!;
        Thread.Sleep(1600);
        var hNote = FindByClass("Notepad");
        if (hNote == IntPtr.Zero) { Console.WriteLine("[SKIP] 找不到记事本窗口"); return; }
        N.StageNotepad(hNote, 1100, 700);
        N.GetWindowRect(hNote, out var nr2);
        Console.WriteLine($"[stage] 记事本 {nr2.Left},{nr2.Top} {nr2.Right - nr2.Left}x{nr2.Bottom - nr2.Top}");

        foreach (var p in Process.GetProcessesByName("iTu")) { try { p.Kill(); } catch { } }
        Thread.Sleep(400);
        var logPath = Path.Combine(Path.GetTempPath(), "itu.log");
        File.Delete(logPath);
        var app = Process.Start(exe)!;
        Thread.Sleep(2200);

        var activation = N.FindWindow(null, "iTu");
        if (activation == IntPtr.Zero) { Console.WriteLine("[SKIP] iTu 未就绪"); return; }

        var dir = SaveDir();
        var region = new Rectangle(RecSel[0], RecSel[1], RecSel[2] - RecSel[0], RecSel[3] - RecSel[1]);
        Console.WriteLine("[dir] " + dir);

        // ---------- A：暂停/继续 + 点"停止并保存" ----------
        var beforeA = Snapshot(dir);
        StartRecordCapture();

        if (WaitWindow("iTu 录制", 8000) == IntPtr.Zero)
        {
            Console.WriteLine("[FAIL] A 录制面板没有出现");
            return;
        }
        Thread.Sleep(1400);

        var geo = ParseGeometry();
        if (!geo.ContainsKey("panel") || !geo.ContainsKey("停止") || !geo.ContainsKey("暂停"))
        {
            Console.WriteLine("[FAIL] 日志里缺少面板/按钮几何");
            return;
        }

        var panel = geo["panel"];
        Console.WriteLine($"[geom] 面板={panel} 区域={region} 重叠={panel.IntersectsWith(region)}");
        Console.WriteLine(panel.IntersectsWith(region)
            ? "[WARN] 面板压在录制区域上（会被录入画面）"
            : "[PASS] 面板自动避开了录制区域");

        Grab(OutDir + "record_1_panel.png", Padded(panel, 30));

        Click(Center(geo["暂停"]));
        N.SetCursorPos(1500, 40);            // 鼠标移开，避免按钮悬停态影响像素比对
        Thread.Sleep(900);
        Grab(OutDir + "record_2_paused_a.png", panel);
        Thread.Sleep(1000);
        Grab(OutDir + "record_2_paused_b.png", panel);
        int diff = DiffPixels(OutDir + "record_2_paused_a.png", OutDir + "record_2_paused_b.png");
        Console.WriteLine($"[check] 暂停 1 秒内面板像素差={diff}");
        Console.WriteLine(diff <= 40 ? "[PASS] 暂停后计时冻结" : "[FAIL] 暂停后界面仍在变化");

        Click(Center(geo["暂停"]));          // 同一个按钮，文字已变成"继续"
        Thread.Sleep(1600);
        Click(Center(geo["停止"]));

        bool closed = WaitGone("iTu 录制", 25000);
        Console.WriteLine(closed ? "[PASS] 停止后面板关闭" : "[FAIL] 停止后面板未关闭");
        Thread.Sleep(600);

        var outA = NewFile(beforeA, dir, "*_录制.*");
        if (outA == null) Console.WriteLine("[FAIL] A 没有生成录制文件");
        else { Console.WriteLine("[file] " + outA); VerifyOutput(outA); }

        // ---------- B：录制热键 = 停止并保存 ----------
        bool hotkeyOk = File.Exists(logPath) &&
                        File.ReadAllLines(logPath).Any(l => l.Contains("[hotkey] 已注册"));
        if (!hotkeyOk)
        {
            Console.WriteLine("[SKIP] 录制热键未注册，跳过 B 用例");
        }
        else
        {
            var beforeB = Snapshot(dir);
            StartRecordCapture();
            if (WaitWindow("iTu 录制", 8000) == IntPtr.Zero) Console.WriteLine("[FAIL] B 录制面板没有出现");
            else
            {
                Thread.Sleep(1600);
                var sink = N.FindWindow(null, "iTuHotkeySink");
                N.PostMessage(sink, 0x0312, (IntPtr)2, IntPtr.Zero);   // 第 2 个注册的热键 = 录制

                bool closedB = WaitGone("iTu 录制", 25000);
                Console.WriteLine(closedB ? "[PASS] 热键再次按下＝停止并保存" : "[FAIL] 热键没有停止录制");
                Thread.Sleep(600);

                var outB = NewFile(beforeB, dir, "*_录制.*");
                if (outB == null) Console.WriteLine("[FAIL] B 没有生成录制文件");
                else { Console.WriteLine("[file] " + outB); VerifyOutput(outB); }
            }
        }

        // ---------- C：取消不留残留 ----------
        var beforeC = Snapshot(dir);
        StartRecordCapture();
        if (WaitWindow("iTu 录制", 8000) == IntPtr.Zero)
        {
            Console.WriteLine("[FAIL] C 录制面板没有出现");
        }
        else
        {
            Thread.Sleep(1300);
            geo = ParseGeometry();
            Click(Center(geo["取消"]));

            bool closedC = WaitGone("iTu 录制", 25000);
            Console.WriteLine(closedC ? "[PASS] 取消后面板关闭" : "[FAIL] 取消后面板未关闭");
            Thread.Sleep(1200);

            var stray = NewFile(beforeC, dir, "*_录制.*");
            Console.WriteLine(stray == null
                ? "[PASS] 取消后没有残留文件"
                : "[FAIL] 取消后留下了 " + stray);
        }

        Grab(OutDir + "record_3_final.png", new Rectangle(0, 0, 1000, 700));

        Console.WriteLine("[log]");
        if (File.Exists(logPath))
            foreach (var line in File.ReadAllLines(logPath))
                if (line.Contains("[record]") || line.Contains("[hotkey]"))
                    Console.WriteLine("  " + line);

        try { app.Kill(); } catch { }
        try { note.Kill(); } catch { }
        Console.WriteLine("[OK] record");
    }

    /// <summary>
    /// 单次录制，专测编码分支：
    ///   builtinGif=true  → 启动 iTu 时从 PATH 里摘掉 ffmpeg，逼它走内置 GIF89a 编码器
    ///   mp4=true         → 临时把设置里的 RecordFormat 改成 Mp4，测完原样还原
    /// </summary>
    static void RunCodec(string exe, bool builtinGif, bool mp4, bool escStop = false)
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iTu", "settings.json");
        string? backup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;

        if (mp4)
        {
            if (backup == null) { Console.WriteLine("[SKIP] 没有 settings.json"); return; }
            File.WriteAllText(settingsPath, backup.Replace("\"RecordFormat\": \"Gif\"", "\"RecordFormat\": \"Mp4\""));
        }

        try
        {
            foreach (var p in Process.GetProcessesByName("iTu")) { try { p.Kill(); } catch { } }
            Thread.Sleep(400);
            var logPath = Path.Combine(Path.GetTempPath(), "itu.log");
            File.Delete(logPath);

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (builtinGif)
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.Environment["PATH"] = string.Join(';', path
                    .Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Where(d => !File.Exists(Path.Combine(d.Trim(), "ffmpeg.exe"))));
            }

            var app = Process.Start(psi)!;
            Thread.Sleep(2200);

            if (N.FindWindow(null, "iTu") == IntPtr.Zero) { Console.WriteLine("[SKIP] iTu 未就绪"); return; }

            var dir = SaveDir();
            var before = Snapshot(dir);
            StartRecordCapture();

            if (WaitWindow("iTu 录制", 8000) == IntPtr.Zero)
            {
                Console.WriteLine("[FAIL] 录制面板没有出现");
                return;
            }
            Thread.Sleep(1800);

            var geo = ParseGeometry();
            if (!geo.ContainsKey("停止")) { Console.WriteLine("[FAIL] 缺少按钮几何"); return; }

            if (escStop)
            {
                // 面板拿到焦点后 Esc 应当等于"停止并保存"
                var panel = N.FindWindow(null, "iTu 录制");
                N.PostMessage(panel, 0x0100, (IntPtr)0x1B, IntPtr.Zero);   // WM_KEYDOWN VK_ESCAPE
                Thread.Sleep(60);
                N.PostMessage(panel, 0x0101, (IntPtr)0x1B, IntPtr.Zero);   // WM_KEYUP
                Console.WriteLine("[act] 已向面板发送 Esc");
            }
            else
            {
                Click(Center(geo["停止"]));
            }
            bool closed = WaitGone("iTu 录制", 25000);
            Console.WriteLine(closed ? "[PASS] 停止后面板关闭" : "[FAIL] 停止后面板未关闭");
            Thread.Sleep(700);

            var file = NewFile(before, dir, mp4 ? "*_录制.mp4" : "*_录制.gif");
            if (file == null) Console.WriteLine("[FAIL] 没有生成" + (mp4 ? " mp4" : " gif"));
            else { Console.WriteLine("[file] " + file); VerifyOutput(file); }

            Console.WriteLine("[log]");
            if (File.Exists(logPath))
                foreach (var line in File.ReadAllLines(logPath))
                    if (line.Contains("[record]")) Console.WriteLine("  " + line);

            try { app.Kill(); } catch { }
        }
        finally
        {
            if (mp4 && backup != null) File.WriteAllText(settingsPath, backup);
            Console.WriteLine("[OK] " + (builtinGif ? "record-builtin" : mp4 ? "record-mp4" : "record-esc"));
        }
    }
}
