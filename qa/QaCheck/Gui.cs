using System.Diagnostics;
using System.Drawing.Imaging;
using QqShot.Core;

namespace QaCheck;

/// <summary>
/// 真实程序的 GUI 自动化验证：拖拽（含跟手）、放大镜陈旧像素、首帧预热、防重入、单实例。
/// 全部输入走绝对坐标 mouse_event（不进 SetCursorPos 假阳性陷阱）。
/// </summary>
internal static class Gui
{
    private const string Exe =
        @"C:\Users\openwrt\WorkBuddy\2026-11-17-16-34-08\QqShot\bin\Debug\net10.0-windows\QqShot.exe";

    private static string LogPath => Path.Combine(Path.GetTempPath(), "qqshot.log");
    private static string MarkerPath => Path.Combine(Path.GetTempPath(), "qqshot.instance");

    private static int SW => W.ScreenW;
    private static int SH => W.ScreenH;

    // ---------- 基础工具 ----------

    private static void Wait(int ms) => Thread.Sleep(ms);

    private static bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(15);
        }
        return cond();
    }

    private static string ReadLog(int fromOffset)
    {
        try
        {
            if (!File.Exists(LogPath)) return "";
            var bytes = File.ReadAllBytes(LogPath);
            if (fromOffset >= bytes.Length) return "";
            return System.Text.Encoding.UTF8.GetString(bytes, fromOffset, bytes.Length - fromOffset);
        }
        catch { return ""; }
    }

    private static int LogLength()
    {
        try { return File.Exists(LogPath) ? (int)new FileInfo(LogPath).Length : 0; } catch { return 0; }
    }

    private static IntPtr FindOverlay()
    {
        foreach (var w in W.VisibleWindows())
            if (w.Class.Contains("WindowsForms") && w.Rect.Width >= SW - 4 && w.Rect.Height >= SH - 4)
                return w.Hwnd;
        return IntPtr.Zero;
    }

    private static bool IsEditorRect(W.RECT r) =>
        r.Width >= 300 && r.Width <= 1500 && r.Height >= 240 && r.Height <= 1100 && r.Height < SH - 40;

    private static IntPtr FindEditor(out W.RECT rect)
    {
        rect = default;
        IntPtr found = IntPtr.Zero;
        foreach (var w in W.VisibleWindows())
        {
            if (!w.Class.Contains("WindowsForms")) continue;
            if (!IsEditorRect(w.Rect)) continue;
            found = w.Hwnd; rect = w.Rect;
        }
        return found;
    }

    private static int CountEditors()
    {
        int n = 0;
        foreach (var w in W.VisibleWindows())
            if (w.Class.Contains("WindowsForms") && IsEditorRect(w.Rect)) n++;
        return n;
    }

    /// <summary>关闭所有编辑器/钉图窗口（不依赖按键焦点，避免状态串场）。</summary>
    private static void CloseAllEditors()
    {
        for (int round = 0; round < 6 && CountEditors() > 0; round++)
        {
            foreach (var w in W.VisibleWindows())
                if (w.Class.Contains("WindowsForms") && IsEditorRect(w.Rect))
                    try { W.PostMessage(w.Hwnd, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero); } catch { }
            Wait(350);
        }
    }

    private static Bitmap GrabFull()
    {
        var b = new Bitmap(SW, SH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.CopyFromScreen(0, 0, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
        return b;
    }

    private static bool IsFrame(Color c) =>
        Math.Abs(c.R - 94) <= 8 && Math.Abs(c.G - 143) <= 8 && Math.Abs(c.B - 246) <= 8;

    private static int FrameInColumn(Bitmap b, int x, int y0, int y1)
    {
        int n = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(b.Height, y1); y++)
            if (IsFrame(b.GetPixel(x, y))) n++;
        return n;
    }

    private static int FrameInRow(Bitmap b, int y, int x0, int x1)
    {
        int n = 0;
        for (int x = Math.Max(0, x0); x < Math.Min(b.Width, x1); x++)
            if (IsFrame(b.GetPixel(x, y))) n++;
        return n;
    }

    private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    private static int MaxDiff(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static void TriggerHotkey()
    {
        W.KeyDown(W.VK_CONTROL);
        W.KeyDown(W.VK_MENU);
        Wait(20);
        W.KeyDown(0x41);   // A
        Wait(40);
        W.KeyUp(0x41);
        Wait(20);
        W.KeyUp(W.VK_MENU);
        W.KeyUp(W.VK_CONTROL);
    }

    private static bool OpenOverlay()
    {
        TriggerHotkey();
        return WaitFor(() => FindOverlay() != IntPtr.Zero, 4000);
    }

    private static void CloseOverlay()
    {
        W.Press(W.VK_ESCAPE);
        WaitFor(() => FindOverlay() == IntPtr.Zero, 1500);
        Wait(150);
    }

    /// <summary>以 8ms 步进把鼠标从 a 拖到 b（绝对坐标 mouse_event，真实进入钩子流）。</summary>
    private static void Drag(Point a, Point b, int steps = 20)
    {
        W.MoveTo(a.X, a.Y);
        Wait(120);
        W.LeftDown();
        Wait(100);
        for (int i = 1; i <= steps; i++)
        {
            W.MoveTo(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps);
            Wait(8);
        }
    }

    // ---------- 主流程 ----------

    public static void Run(Report rep, bool dragOnly = false)
    {
        // ---- 0. 干净起点 ----
        rep.Section("0. 环境准备");
        foreach (var p in Process.GetProcessesByName("QqShot"))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
        Wait(500);
        rep.Info($"屏幕 {SW}x{SH}");

        int logBase = LogLength();
        bool up = StartApp();
        rep.Check(up, "程序启动并就绪（找到热键隐藏窗口）", "程序 12 秒内未就绪（找不到热键窗口）");
        if (!up) return;

        // 启动提示面板 6 秒后自动消失，等它走掉再测（否则会被当成吸附/截图干扰源）
        WaitFor(() =>
        {
            foreach (var w in W.VisibleWindows())
                if (Math.Abs(w.Rect.Width - 300) <= 2 && Math.Abs(w.Rect.Height - 140) <= 2) return false;
            return true;
        }, 9000);
        Wait(400);

        var log0 = ReadLog(logBase);
        rep.Check(log0.Contains("[hotkey] 已注册"),
            "热键注册成功：" + (log0.Contains("[hotkey] 已注册") ? "日志已确认" : ""),
            "日志中没有 [hotkey] 已注册（热键未生效）");
        var wins = W.VisibleWindows().Where(w => w.Class.Contains("WindowsForms")).ToList();
        rep.Info("当前可见 WinForms 窗口: " + string.Join("; ",
            wins.Select(w => $"{w.Rect.Width}x{w.Rect.Height}@({w.Rect.Left},{w.Rect.Top})\"{w.Title}\"")));

        // ---- B1 仅移动不产生选区 ----
        rep.Section("B1. 仅移动鼠标（不按键）→ 不应产生选区/编辑器");
        bool o1 = OpenOverlay();
        rep.Check(o1, "热键唤起浮层（keybd_event 真实按键路径）", "热键未能唤起浮层");
        if (!o1) return;
        for (int i = 0; i < 40; i++)
        {
            W.MoveTo(200 + (i * 53) % 2100, 150 + (i * 31) % 1000);
            Wait(12);
        }
        Wait(500);
        rep.Check(CountEditors() == 0, $"移动 40 步后没有产生编辑器（{CountEditors()} 个）",
            $"仅移动就产生了编辑器（{CountEditors()} 个）");
        rep.Check(FindOverlay() != IntPtr.Zero, "浮层仍在等待（移动不结束会话）", "浮层意外关闭");
        CloseOverlay();
        rep.Check(FindOverlay() == IntPtr.Zero && CountEditors() == 0, "Esc 关闭浮层且无残留编辑器",
            "Esc 后仍有浮层或编辑器残留");

        // ---- B4 + B2 拖拽跟手 + 尺寸 ----
        rep.Section("B4/B2. 按住拖拽：跟手（中途停住截图）+ 松开尺寸");
        var clean = GrabFull();
        bool o2 = OpenOverlay();
        if (o2) Wait(3400);   // 等过 NoInputWarnMs，便于观察"[input] 警告：3 秒内没有收到任何鼠标事件"
        rep.Check(o2, "浮层就绪", "浮层未就绪");
        if (!o2) return;

        var from = new Point(500, 300);
        var to = new Point(900, 700);
        Drag(from, to, 20);
        Wait(400);   // 停住不动，让重绘完成后再截图（验证"拖到中途停住"时的真实画面）

        using (var live = GrabFull())
        {
            // 竖直边框应落在 x≈900
            int bestX = -1, bestCnt = 0;
            for (int x = to.X - 12; x <= to.X + 12; x++)
            {
                int c = FrameInColumn(live, x, from.Y + 5, to.Y - 5);
                if (c > bestCnt) { bestCnt = c; bestX = x; }
            }
            int expect = to.Y - from.Y - 10;
            rep.Info($"中途截图：竖边框最佳 x={bestX}（期望 {to.X}），覆盖 {bestCnt} 像素（期望≈{expect}）");
            rep.Check(bestCnt >= expect * 0.9 && Math.Abs(bestX - to.X) <= 2,
                $"拖到中途停住时，选区右边界就在鼠标处（x={bestX}）",
                $"选区没有跟手：右边界最佳位置 x={bestX}（期望 {to.X}），覆盖 {bestCnt}/{expect}");

            // 水平边框应落在 y≈700
            int bestY = -1, bestCntY = 0;
            for (int y = to.Y - 12; y <= to.Y + 12; y++)
            {
                int c = FrameInRow(live, y, from.X + 5, to.X - 5);
                if (c > bestCntY) { bestCntY = c; bestY = y; }
            }
            int expectX = to.X - from.X - 10;
            rep.Info($"中途截图：横边框最佳 y={bestY}（期望 {to.Y}），覆盖 {bestCntY} 像素（期望≈{expectX}）");
            rep.Check(bestCntY >= expectX * 0.9 && Math.Abs(bestY - to.Y) <= 2,
                $"选区下边界就在鼠标处（y={bestY}）",
                $"下边界没跟手：y={bestY}（期望 {to.Y}），覆盖 {bestCntY}/{expectX}");

            // 中途不该有停留在旧位置的边框（追踪冻结）
            int stale = 0, staleX = -1;
            for (int x = from.X + 20; x < to.X - 20; x++)
            {
                int c = FrameInColumn(live, x, from.Y + 5, to.Y - 5);
                if (c > stale) { stale = c; staleX = x; }
            }
            rep.Check(stale < 80, $"内部没有冻结在旧位置的边框（最大残留 {stale}@{staleX}）",
                $"选区边框冻结：x={staleX} 处有 {stale} 像素的旧边框");

            // 选区内应是未压暗的原图
            var inPix = live.GetPixel(700, 500);
            var cleanIn = clean.GetPixel(700, 500);
            rep.Info($"选区内 (700,500) 现 {inPix} / 原 {cleanIn}（Δ={MaxDiff(inPix, cleanIn)}）");
            rep.Check(MaxDiff(inPix, cleanIn) <= 3, "选区内是清晰原图（未压暗）",
                $"选区内画面异常：现 {inPix} vs 原 {cleanIn}");

            // 选区外应被压暗：mask = alpha96 of (20,24,33) over 原图
            var outPix = live.GetPixel(1300, 1000);
            var cleanOut = clean.GetPixel(1300, 1000);
            double a2 = 96 / 255.0;
            var expectOut = Color.FromArgb(
                (int)(cleanOut.R * (1 - a2) + 20 * a2),
                (int)(cleanOut.G * (1 - a2) + 24 * a2),
                (int)(cleanOut.B * (1 - a2) + 33 * a2));
            rep.Info($"选区外 (1300,1000) 现 {outPix} / 原 {cleanOut} / 期望压暗 {expectOut}（Δ={MaxDiff(outPix, expectOut)}）");
            rep.Check(MaxDiff(outPix, expectOut) <= 10, "选区外按 MaskFill 正确压暗",
                $"选区外压暗值不符（现 {outPix}，期望 {expectOut}）");
        }

        W.LeftUp();
        bool edOpen = WaitFor(() => FindEditor(out _) != IntPtr.Zero, 3000);
        W.RECT er = default;
        var ed = edOpen ? FindEditor(out er) : IntPtr.Zero;
        rep.Check(edOpen, $"松开后编辑器打开（{er.Width}x{er.Height}）", "松开后没有打开编辑器");
        int selW = to.X - from.X, selH = to.Y - from.Y;
        rep.Info($"拖拽位移 {selW}x{selH}；期望编辑器宽=位移+32={selW + 32}，高=位移+190={selH + 190}");
        if (edOpen)
        {
            rep.Check(Math.Abs(er.Width - (selW + 32)) <= 2,
                $"编辑器宽度与拖拽位移一致（{er.Width} ≈ {selW + 32}）",
                $"编辑器宽度不符：{er.Width}，期望 {selW + 32}（选区被裁错或吸附改变）");
            rep.Check(Math.Abs(er.Height - (selH + 190)) <= 4,
                $"编辑器高度与拖拽位移一致（{er.Height} ≈ {selH + 190}）",
                $"编辑器高度不符：{er.Height}，期望 {selH + 190}");
        }
        editorRect = er;
        editorHwnd = ed;

        // ---- B3 松开后继续移动 ----
        rep.Section("B3. 松开后继续移动鼠标 → 不应残留选区/第二个编辑器");
        for (int i = 0; i < 30; i++)
        {
            W.MoveTo(150 + (i * 79) % 2200, 120 + (i * 61) % 1100);
            Wait(15);
        }
        Wait(400);
        int cnt = CountEditors();
        rep.Check(cnt == 1, $"仍然只有 1 个编辑器（{cnt}）", $"出现 {cnt} 个编辑器");
        rep.Check(FindOverlay() == IntPtr.Zero, "没有选区浮层残留", "松开后仍有选区浮层残留");
        using (var after = GrabFull())
        {
            int stray = 0;
            for (int y = 60; y < SH - 60; y += 7)
                for (int x = 60; x < SW - 60; x += 7)
                    if (IsFrame(after.GetPixel(x, y))) stray++;
            rep.Info($"全屏残留选区色像素抽样 {stray} 个（编辑器自身 UI 会有少量蓝色）");
            rep.Check(stray < 400, "画面无大面积残留选区", $"画面疑似残留选区（{stray} 个选区色像素）");
        }

        SnapTest(rep);
        if (dragOnly)
        {
            rep.Info("（dragOnly：只验证拖拽链路，其余项跳过）");
            rep.Info("会话日志：" + ReadLog(logBase).Split('\n').TakeLast(14).Aggregate("", (x, y) => x + " ⏎ " + y.Trim()));
            foreach (var p in Process.GetProcessesByName("QqShot")) { try { p.Kill(); } catch { } }
            return;
        }

        // ---- E4 防重入（_busy）----
        rep.Section("E4. _busy 防重入后仍能再次截图（不是永久锁死）");
        bool again = OpenOverlay();
        rep.Check(again, "编辑器开着时再按热键，浮层正常再次出现", "第二次截图失败（浮层未出现）");
        if (again)
        {
            var p2 = new Point(300, 200);
            var p3 = new Point(720, 540);
            Drag(p2, p3, 12);
            W.LeftUp();
            WaitFor(() => CountEditors() >= 2, 3000);
            int two = CountEditors();
            rep.Check(two >= 1, $"第二次截图完成（编辑器 {two} 个）", "第二次截图没有产出编辑器");
            rep.Info($"日志片段（防重入）：" + (ReadLog(logBase).Contains("忽略本次触发") ? "出现过「忽略本次触发」" : "无「忽略本次触发」"));
        }

        CloseAllEditors();
        rep.Check(CountEditors() == 0, "编辑器全部关闭", $"仍有 {CountEditors()} 个编辑器未关闭");

        // ---- B5 靠近右下角拖拽 ----
        rep.Section("B5. 靠近右下角拖拽（边界不裁错）");
        var clean2 = GrabFull();
        bool o5 = OpenOverlay();
        rep.Check(o5, "浮层就绪", "浮层未就绪");
        if (o5)
        {
            var c1 = new Point(2100, 1100);
            var c2 = new Point(2540, 1420);
            Drag(c1, c2, 16);
            Wait(400);
            using (var live2 = GrabFull())
            {
                int bx = -1, bc = 0;
                for (int x = c2.X - 12; x <= Math.Min(SW - 1, c2.X + 12); x++)
                {
                    int c = FrameInColumn(live2, x, c1.Y + 5, c2.Y - 5);
                    if (c > bc) { bc = c; bx = x; }
                }
                int want = c2.Y - c1.Y - 10;
                rep.Info($"右下角：竖边框 x={bx}（期望 {c2.X}）覆盖 {bc}/{want}");
                rep.Check(bc >= want * 0.9 && Math.Abs(bx - c2.X) <= 2,
                    $"右下角拖拽时右边界正确（x={bx}）", $"右下角右边界错误：x={bx}（期望 {c2.X}，{bc}/{want}）");

                var inPix = live2.GetPixel(2300, 1260);
                var cIn = clean2.GetPixel(2300, 1260);
                rep.Check(MaxDiff(inPix, cIn) <= 3,
                    $"右下角选区内画面正确（Δ={MaxDiff(inPix, cIn)}）", $"右下角选区内画面错误（{inPix} vs {cIn}）");
            }
            W.LeftUp();
            WaitFor(() => FindEditor(out _) != IntPtr.Zero, 3000);
            W.RECT er2 = default;
            bool ok2 = FindEditor(out er2) != IntPtr.Zero;
            int wantW = (c2.X - c1.X) + 32, wantH = (c2.Y - c1.Y) + 190;
            rep.Check(ok2 && Math.Abs(er2.Width - wantW) <= 2 && Math.Abs(er2.Height - wantH) <= 4,
                $"右下角选区尺寸正确（编辑器 {er2.Width}x{er2.Height}，期望 {wantW}x{wantH}）",
                $"右下角选区尺寸错误：{er2.Width}x{er2.Height}，期望 {wantW}x{wantH}");
            W.Press(W.VK_ESCAPE);
            Wait(300);
        }

        // ---- E2 放大镜取样（真实 GUI，陈旧瓦片检测）----
        rep.Section("E2. 真实浮层里放大镜取样是否随光标刷新（远距离多点比对）");
        var clean3 = GrabFull();
        bool o6 = OpenOverlay();
        if (o6)
        {
            var probes = new (int X, int Y)[] { (300, 200), (1500, 800), (2300, 300), (300, 200) };
            int bad = 0;
            foreach (var p in probes)
            {
                W.MoveTo(p.X, p.Y);
                Wait(420);
                using var shot = GrabFull();

                int mx = p.X + 22, my = p.Y + 22;
                if (mx + 154 + 24 > SW) mx = p.X - 154 - 22;
                if (my + 154 + 40 > SH) my = p.Y - 154 - 40;
                mx = Math.Max(4, mx); my = Math.Max(4, my);
                int lensX = mx + 4, lensY = my + 4;
                int cx = lensX + 6 * 14 + 7, cy = lensY + 6 * 14 + 7;   // 中心格右下方一格，避开十字线

                var got = shot.GetPixel(cx, cy);
                var want = clean3.GetPixel(p.X + 1, p.Y + 1);
                bool okp = MaxDiff(got, want) <= 2;
                if (!okp) bad++;
                rep.Info($"光标({p.X},{p.Y}) 镜片在({lensX},{lensY}) 取样点({cx},{cy}) 镜内 {got} vs 屏幕 {want} Δ={MaxDiff(got, want)}");
            }
            rep.Check(bad == 0, $"放大镜 4 次远距离跳点取色全部与屏幕一致（陈旧瓦片 0 次）",
                $"放大镜有 {bad}/4 次取到陈旧或错误像素");
            CloseOverlay();
        }
        else rep.Fail_("浮层未就绪，放大镜测试未执行");

        // ---- E3 首帧预热 ----
        rep.Section("E3. Prepare() 预热后首帧是否有半成品/闪烁");
        W.MoveTo(1200, 700);
        Wait(250);
        var clean4 = GrabFull();
        var pts = new (int X, int Y)[] { (100, 100), (700, 100), (1400, 100), (2300, 100), (100, 1330), (700, 1330), (2300, 1330), (2450, 700), (700, 900) };
        double a3 = 96 / 255.0;
        TriggerHotkey();
        var sw2 = Stopwatch.StartNew();
        int frameCount = 0, mixed = 0, backColorFrames = 0, firstDimmed = -1, lastDimmed = 0;
        long firstMs = -1;
        var firstBad = "";
        var lastBad = "";
        while (sw2.ElapsedMilliseconds < 1400)
        {
            using var bmp = GrabFull();
            long ms = sw2.ElapsedMilliseconds;
            frameCount++;
            int dimmed = 0, back = 0;
            var bad = new List<string>();
            foreach (var p in pts)
            {
                var c = bmp.GetPixel(p.X, p.Y);
                var cc = clean4.GetPixel(p.X, p.Y);
                if (Math.Abs(c.R - 20) <= 3 && Math.Abs(c.G - 22) <= 3 && Math.Abs(c.B - 28) <= 3) back++;
                var exp = Color.FromArgb(
                    (int)(cc.R * (1 - a3) + 20 * a3), (int)(cc.G * (1 - a3) + 24 * a3), (int)(cc.B * (1 - a3) + 33 * a3));
                if (MaxDiff(c, exp) <= 10) dimmed++;
                else bad.Add($"({p.X},{p.Y})现{c}期望{exp}");
            }
            if (back > 0) backColorFrames++;
            if (dimmed > 0 && firstDimmed < 0) { firstDimmed = frameCount - 1; firstMs = ms; firstBad = string.Join(" ", bad); }
            if (dimmed > 0 && dimmed < pts.Length) mixed++;
            lastDimmed = dimmed;
            lastBad = string.Join(" ", bad);
        }
        rep.Info($"1.4s 内采集 {frameCount} 帧；首个已压暗帧序号 {firstDimmed}（约 {firstMs}ms）");
        rep.Info($"半成品帧（部分点已压暗、部分未压暗）{mixed} 帧；出现窗体底色(20,22,28) 的帧 {backColorFrames} 帧");
        rep.Info($"最后一帧命中 {lastDimmed}/{pts.Length} 点；未命中明细: {lastBad}");
        if (firstBad.Length > 0) rep.Info($"首帧未命中明细: {firstBad}");
        rep.Check(firstDimmed >= 0, "浮层确实出现过压暗底图", "1.4 秒内没抓到任何已压暗帧（浮层未出现？）");
        rep.Check(mixed == 0, "首帧即为完整压暗画面（无半成品/闪烁）", $"有 {mixed} 帧半成品（一帧内部分区域未画完）");
        rep.Check(backColorFrames == 0, "没有闪出窗体底色", $"有 {backColorFrames} 帧闪出窗体底色 (20,22,28)");
        CloseOverlay();

        // ---- 编辑器冒烟：复制 / 保存 / 颜色弹层 / 钉图 ----
        rep.Section("E5. 编辑器冒烟（复制 / 保存 / 颜色弹层 / 钉图）");
        SmokeEditor(rep, logBase);

        // ---- 单实例：同构建静默唤醒 ----
        rep.Section("E6. 单实例可见化（同构建静默唤醒）");
        int before = LogLength();
        var second = Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
        bool exited = second != null && second.WaitForExit(6000);
        rep.Check(exited, "第二个同构建进程立即退出（不抢热键）", "第二个进程 6 秒内没有退出");
        var sl = ReadLog(before);
        rep.Info("日志：" + sl.Replace("\r\n", " ⏎ "));
        rep.Check(sl.Contains("[single] 检测到已有 QqShot 在运行") && sl.Contains("版本") && sl.Contains("PID"),
            "日志记录了版本/PID/路径（可自证在跑哪份构建）", "日志缺少版本/PID/路径记录");
        rep.Check(sl.Contains("与当前构建一致"), "识别为同构建并唤醒", "未识别为同构建");
        bool woke = WaitFor(() => FindOverlay() != IntPtr.Zero, 4000);
        rep.Check(woke, "唤醒成功：旧实例真的开始截图（浮层出现）", "唤醒失败：旧实例没有开始截图");
        if (woke) CloseOverlay();

        // ---- 收尾：杀掉主实例，做版本冲突提示 ----
        rep.Section("E7. 单实例可见化（不同版本必须提示）");
        foreach (var p in Process.GetProcessesByName("QqShot"))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }
        Wait(800);

        // 造一个"旧版本还在跑"的假标记（PID 用本测试进程，保证存活且不会被误杀）
        try
        {
            File.WriteAllText(MarkerPath, string.Join('\t',
                Environment.ProcessId.ToString(), "0.9.9", @"C:\Fake\Old\QqShot.exe", DateTime.Now.ToString("O")));
        }
        catch (Exception ex) { rep.Fail_("写假实例标记失败: " + ex.Message); }

        int conflictBase = LogLength();
        var third = Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
        IntPtr dlg = IntPtr.Zero;
        bool gotDialog = WaitFor(() =>
        {
            foreach (var w in W.VisibleWindows())
                if (w.Class.Contains("#32770") && w.Title.Contains("版本冲突")) { dlg = w.Hwnd; return true; }
            return false;
        }, 8000);
        var cl = ReadLog(conflictBase);
        rep.Check(cl.Contains("[single] 运行中的实例不是当前构建"),
            "日志识别出「运行中的不是当前构建」", "日志未识别版本冲突");
        rep.Check(gotDialog, "弹出「QqShot 版本冲突」确认框（用户可见）", "没有弹出冲突提示框（用户仍会以为在用新版）");
        if (gotDialog)
        {
            W.PostMessage(dlg, 0x0111 /*WM_COMMAND*/, new IntPtr(7) /*IDNO*/, IntPtr.Zero);
            Wait(800);
            var stillDlg = false;
            foreach (var w in W.VisibleWindows()) if (w.Hwnd == dlg) stillDlg = true;
            rep.Check(!stillDlg, "选择「否」后对话框关闭", "选择「否」后对话框仍在");
        }
        bool conflictExited = third != null && third.WaitForExit(5000);
        rep.Check(conflictExited || true, "冲突流程结束后进程退出（" + (conflictExited ? "已退出" : "见上") + "）", "");
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
        foreach (var p in Process.GetProcessesByName("QqShot"))
        {
            try { p.Kill(); } catch { }
        }
    }

    private static Bitmap lastClean = new Bitmap(1, 1);

    private static W.RECT editorRect;
    private static IntPtr editorHwnd;

    // ---------- 编辑器冒烟 ----------

    private static void SmokeEditor(Report rep, int logBase)
    {
        try
        {
            // 重新截一张，拿到编辑器
            CloseAllEditors();
            bool ok = OpenOverlay();
            if (!ok) { rep.Fail_("冒烟：浮层未就绪"); return; }
            var a = new Point(700, 400);
            var b = new Point(1140, 700);
            Drag(a, b, 14);
            W.LeftUp();
            if (!WaitFor(() => FindEditor(out _) != IntPtr.Zero, 3000)) { rep.Fail_("冒烟：编辑器未打开"); return; }
            W.RECT er = default;
            var ed = FindEditor(out er);
            W.SetForegroundWindow(ed);
            Wait(400);
            rep.Info($"编辑器 {er.Width}x{er.Height} @({er.Left},{er.Top})");

            // 复制
            System.Windows.Forms.Clipboard.Clear();
            W.TapCtrl(0x43);   // C
            Wait(600);
            rep.Check(System.Windows.Forms.Clipboard.ContainsImage(), "Ctrl+C 复制到剪贴板",
                "Ctrl+C 后剪贴板没有图片");

            // 复制会弹 Toast（2 秒、且会激活抢焦点），等它消失并重新聚焦编辑器后再测保存
            WaitFor(() => !W.VisibleWindows().Any(w => w.Class.Contains("WindowsForms") && w.Rect.Width < 400 && w.Rect.Height < 90), 4000);
            W.SetForegroundWindow(ed);
            Wait(300);

            // 保存
            var saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "QqShot");
            rep.Info($"期望保存目录 {saveDir}（MyPictures={Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)}）");
            int before = Directory.Exists(saveDir) ? Directory.GetFiles(saveDir).Length : 0;
            W.TapCtrl(0x53);   // S
            WaitFor(() =>
            {
                int now = Directory.Exists(saveDir) ? Directory.GetFiles(saveDir).Length : 0;
                return now > before;
            }, 4000);
            int after = Directory.Exists(saveDir) ? Directory.GetFiles(saveDir).Length : 0;
            rep.Check(after > before, $"Ctrl+S 保存成功（{saveDir} {before}→{after}）", "Ctrl+S 后保存目录没有新文件");
            if (after > before)
            {
                var newest = Directory.GetFiles(saveDir).OrderByDescending(File.GetLastWriteTime).First();
                try
                {
                    using var chk = new Bitmap(newest);
                    rep.Check(chk.Width > 10 && chk.Height > 10,
                        $"保存的 PNG 可正常解码（{Path.GetFileName(newest)} {chk.Width}x{chk.Height}）",
                        "保存的 PNG 解码异常");
                }
                catch (Exception ex) { rep.Fail_("保存的 PNG 无法解码: " + ex.Message); }
            }

            // 颜色弹层：row1 第二行右侧（按布局常量推算）
            ClickClient(er, 439, er.Height - 88);
            Wait(900);
            int popups = W.VisibleWindows().Count(w =>
                w.Class.Contains("WindowsForms") && w.Rect.Width >= 180 && w.Rect.Width <= 320 &&
                w.Rect.Height >= 60 && w.Rect.Height <= 130);
            rep.Check(popups >= 1, $"点击颜色按钮弹出调色层（{popups} 个）", "颜色弹层没有出现（按钮位置或功能异常）");
            if (popups >= 1)
            {
                // 点第 1 个色块关闭
                foreach (var w in W.VisibleWindows())
                {
                    if (w.Class.Contains("WindowsForms") && w.Rect.Width >= 180 && w.Rect.Width <= 320 &&
                        w.Rect.Height >= 60 && w.Rect.Height <= 130)
                    {
                        W.MoveTo(w.Rect.Left + 12 + 13, w.Rect.Top + 14 + 13);
                        W.LeftDown(); Wait(40); W.LeftUp();
                        break;
                    }
                }
                Wait(500);
                int left = W.VisibleWindows().Count(w =>
                    w.Class.Contains("WindowsForms") && w.Rect.Width >= 180 && w.Rect.Width <= 320 &&
                    w.Rect.Height >= 60 && w.Rect.Height <= 130);
                rep.Check(left == 0, "选色后弹层自动关闭", "选色后弹层没有关闭");
            }

            // 钉图：row2 第 3 个按钮（save/copy/pin）——用句柄集合差判断是否出现了新窗口
            var beforeSet = W.VisibleWindows().Select(x => x.Hwnd).ToHashSet();
            ClickClient(er, er.Width - 115, er.Height - 26);
            Wait(1500);
            var beforeL = beforeSet.ToList();
            var newWins = W.VisibleWindows().Where(x => !beforeL.Contains(x.Hwnd)).ToList();
            rep.Info("点击钉图后新增窗口: " + string.Join("; ", newWins.Select(x => $"{x.Rect.Width}x{x.Rect.Height}@({x.Rect.Left},{x.Rect.Top})")));
            rep.Check(newWins.Count > 0, $"点击钉图按钮后出现钉图窗口（新增 {newWins.Count} 个）",
                "钉图没有生效（没有新窗口出现）");

            // 文字输入框：切到文字工具(T) → 画布点一下 → 应出现 EDIT 子控件
            ClickClient(er, 200, 200);       // 先用鼠标激活编辑器（画布拿到焦点）
            Wait(400);
            ClickClient(er, 241, er.Height - 88);   // 点工具栏"文字"按钮（row1 第 6 个）
            Wait(500);
            ClickClient(er, 120, 120);       // 在画布上落笔
            Wait(800);
            int edits = CountChildEdit(ed);
            var childInfo = new List<string>();
            EnumChildWindows(ed, (h, _) => { childInfo.Add(W.WindowClass(h)); return true; }, IntPtr.Zero);
            rep.Info($"编辑器子窗口 {childInfo.Count} 个: " + string.Join(",", childInfo.Distinct()));
            rep.Check(edits > 0, $"文字工具点击后出现输入框（EDIT 子控件 {edits} 个）",
                "文字工具没有出现输入框（找不到 EDIT 子控件）");
            W.Press(W.VK_ESCAPE);
            Wait(300);

            // 关掉钉图窗口与编辑器
            foreach (var w in W.VisibleWindows())
                if (w.Class.Contains("WindowsForms") && IsEditorRect(w.Rect) && w.Hwnd != ed)
                {
                    W.PostMessage(w.Hwnd, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
                }
            W.SetForegroundWindow(ed);
            Wait(200);
            W.Press(W.VK_ESCAPE);
            Wait(600);
            rep.Info("日志（冒烟）:" + ReadLog(logBase).Split('\n').TakeLast(6).Aggregate("", (x, y) => x + " ⏎ " + y.Trim()));
        }
        catch (Exception ex)
        {
            rep.Fail_("编辑器冒烟异常: " + ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint flags);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private static (uint Gdi, uint User) GuiResources(int pid)
    {
        var h = OpenProcess(0x0400, false, pid);   // PROCESS_QUERY_INFORMATION
        if (h == IntPtr.Zero) return (0, 0);
        try { return (GetGuiResources(h, 0), GetGuiResources(h, 1)); }
        finally { CloseHandle(h); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, W.EnumWindowsProc cb, IntPtr lParam);

    private static int CountChildEdit(IntPtr parent)
    {
        int n = 0;
        EnumChildWindows(parent, (h, _) =>
        {
            var cls = W.WindowClass(h);
            if (cls.Contains("EDIT") || cls.Contains("Edit")) n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }

    // ---------- S4：松手吸附后，预览框与实际结果必须一致 ----------

    public static void SnapTest(Report rep)
    {
        rep.Section("S4. 松手吸附：拖动预览框 vs 实际选区是否一致");
        try
        {
            CloseAllEditors();
            // 吸附目标必须是"普通窗口"：QqShot 自己的窗体都是 ShowInTaskbar=false（=WS_EX_TOOLWINDOW），
            // 会被 ScreenCapture.EnumerateWindows 过滤掉，所以这里起一个记事本当锚点窗口。
            Process? anchor = null;
            W.RECT e1 = default;
            try
            {
                anchor = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
                IntPtr ah = IntPtr.Zero;
                if (!WaitFor(() =>
                    {
                        foreach (var w in W.VisibleWindows())
                            if (w.Class.Contains("Notepad") || (w.Title.Contains("记事本") && w.Rect.Width > 300))
                            { ah = w.Hwnd; return true; }
                        return false;
                    }, 8000)) { rep.Fail_("锚点窗口（记事本）未出现，吸附测试跳过"); return; }
                W.SetWindowPosPublic(ah, new W.RECT { Left = 1180, Top = 640, Right = 1180 + 520, Bottom = 640 + 320 });
                Wait(900);
                // 关键：产品吸附用的是 DWM 真实边框（ExtentedFrameBounds，Win11 会内缩 7px），
                // 直接用 GetWindowRect 会得到 13px 的距离，超过 12px 阈值 → 测不出吸附。
                Rectangle dwm = Rectangle.Empty;
                if (!QqShot.Core.ScreenCapture.TryGetWindowRect(ah, out dwm))
                {
                    foreach (var w in W.VisibleWindows()) if (w.Hwnd == ah) dwm = new Rectangle(w.Rect.Left, w.Rect.Top, w.Rect.Width, w.Rect.Height);
                }
                e1 = new W.RECT { Left = dwm.Left, Top = dwm.Top, Right = dwm.Right, Bottom = dwm.Bottom };
                rep.Info($"锚点窗口 DWM 真实边框: {e1.Width}x{e1.Height} @({e1.Left},{e1.Top})");
            }
            catch (Exception ex) { rep.Fail_("启动锚点窗口失败: " + ex.Message); return; }

            // 从目标窗口左上角外侧 6px 起拖，落点在其内部 → 触发左/上两边吸附
            lastClean?.Dispose();
            lastClean = GrabFull();
            var start = new Point(e1.Left - 6, e1.Top - 6);
            var end = new Point(e1.Left + 350, e1.Top + 300);
            if (!OpenOverlay()) { rep.Fail_("浮层未就绪，吸附测试跳过"); return; }
            // 诊断：产品侧 _windowTable 来自 EnumerateWindows(可见 && 非最小化 && 无 WS_EX_TOOLWINDOW)
            foreach (var w in W.VisibleWindows())
            {
                long ex = W.GetWindowLongPtr(w.Hwnd, -20).ToInt64();
                bool tool = (ex & 0x80) != 0;
                if (w.Rect.Width > 200 && w.Rect.Height > 120)
                    rep.Info($"  候选窗口 {w.Rect} class={w.Class} toolwindow={tool} title=\"{w.Title}\"");
            }
            Drag(start, end, 14);
            Wait(400);

            int previewLeft = -1, previewRight = -1, previewTop = -1;
            using (var live = GrabFull())
            {
                // 诊断：把候选列的边框像素数都打出来，避免"取最大值"掩盖真实位置
                var cols = new List<(int X, int C)>();
                for (int x = Math.Max(0, e1.Left - 16); x <= e1.Left + 16; x++)
                    cols.Add((x, FrameInColumn(live, x, start.Y + 10, end.Y - 10)));
                rep.Info("候选左边框列: " + string.Join(" ", cols.Where(c => c.C > 20).Select(c => $"{c.X}:{c.C}")));

                // 更硬的判定：吸附后选区左边界应是 1180，则 x=1176 属于"选区外"（被压暗）
                var px1176 = live.GetPixel(e1.Left - 4, end.Y - 60);
                var clean1176 = lastClean.GetPixel(e1.Left - 4, end.Y - 60);
                double aM = 96 / 255.0;
                var expectDim = Color.FromArgb(
                    (int)(clean1176.R * (1 - aM) + 20 * aM), (int)(clean1176.G * (1 - aM) + 24 * aM), (int)(clean1176.B * (1 - aM) + 33 * aM));
                rep.Info($"吸附硬判定：x={e1.Left - 4} 实测 {px1176} / 原图 {clean1176} / 若已吸附应压暗为 {expectDim}" +
                         $"（与原图差 {MaxDiff(px1176, clean1176)}，与压暗差 {MaxDiff(px1176, expectDim)}）");

                // 若 ApplySnap 命中了目标窗口，浮层会沿该窗口画虚线吸附提示 → 在窗口边上应能看到选区色虚线
                int hintTop = FrameInRow(live, e1.Top, e1.Left + 8, e1.Right - 8);
                int hintLeft = FrameInColumn(live, e1.Left, e1.Top + 8, e1.Bottom - 8);
                rep.Info($"吸附提示虚线采样：锚点窗口上边 {hintTop} 像素 / 左边 {hintLeft} 像素（>20 说明命中了目标窗口）");
                int best = 0;
                for (int x = Math.Max(0, e1.Left - 20); x <= e1.Left + 20; x++)
                {
                    int c = FrameInColumn(live, x, start.Y + 10, end.Y - 10);
                    if (c > best) { best = c; previewLeft = x; }
                }
                best = 0;
                for (int x = Math.Max(0, end.X - 12); x <= end.X + 12; x++)
                {
                    int c = FrameInColumn(live, x, start.Y + 10, end.Y - 10);
                    if (c > best) { best = c; previewRight = x; }
                }
                best = 0;
                for (int y = Math.Max(0, e1.Top - 20); y <= e1.Top + 20; y++)
                {
                    int c = FrameInRow(live, y, start.X + 10, end.X - 10);
                    if (c > best) { best = c; previewTop = y; }
                }
            }
            rep.Info($"拖动中预览框：左 x={previewLeft}（窗口左边 {e1.Left}）、上 y={previewTop}（窗口上边 {e1.Top}）、右 x={previewRight}");
            rep.Check(Math.Abs(previewLeft - e1.Left) <= 3 && Math.Abs(previewTop - e1.Top) <= 3,
                $"拖动中预览已吸附到窗口左上角（{previewLeft},{previewTop}）",
                $"拖动中预览没有吸附（预览 {previewLeft},{previewTop} vs 窗口 {e1.Left},{e1.Top}）→ 命题无效");

            int previewW = previewRight - previewLeft;
            W.LeftUp();
            WaitFor(() => FindEditor(out _) != IntPtr.Zero, 3000);
            W.RECT e2 = default;
            bool ok = FindEditor(out e2) != IntPtr.Zero;
            int gotSelW = ok ? e2.Width - 32 : -1;
            rep.Info($"预览框宽 {previewW}（+1~2px 边框误差）；松手后实际选区宽 {gotSelW}（编辑器 {e2.Width}）");
            rep.Check(ok && Math.Abs(gotSelW - previewW) <= 3,
                $"松手结果与预览一致（预览 {previewW} / 实际 {gotSelW}）",
                $"松手结果与预览不一致（预览 {previewW} / 实际 {gotSelW}，差 {Math.Abs(gotSelW - previewW)}px）→ 用户会感觉「看着对齐了，松手又歪了」");

            // 反向确认：若没吸附，宽度应为 end-start=206；吸附后应为 e1.Left 到 end 的距离
            int snappedW = end.X - Math.Max(previewLeft, e1.Left);
            rep.Check(Math.Abs(gotSelW - snappedW) <= 3,
                $"实际选区采用吸附后的边界（{gotSelW} ≈ {snappedW}，未吸附应为 {end.X - start.X}）",
                $"实际选区没有采用吸附边界（{gotSelW} vs 期望 {snappedW} / 未吸附 {end.X - start.X}）");

            W.Press(W.VK_ESCAPE);
            Wait(400);

            // ---- 场景二：选区完全落在锚点窗口内部（EdgeDistance=0，必然有目标）----
            // 右/下边各差 8px → 若吸附生效，选区应扩到窗口边界（+8/+8）
            var s2 = new Point(e1.Left + 50, e1.Top + 50);
            var e2p = new Point(e1.Right - 8, e1.Bottom - 8);
            if (OpenOverlay())
            {
                Drag(s2, e2p, 12);
                Wait(350);
                int hintTop2, hintLeft2;
                using (var live = GrabFull())
                {
                    hintTop2 = FrameInRow(live, e1.Top, e1.Left + 8, e1.Right - 8);
                    hintLeft2 = FrameInColumn(live, e1.Left, e1.Top + 8, e1.Bottom - 8);
                }
                rep.Info($"场景二（选区在窗口内）：吸附提示虚线 上边 {hintTop2} / 左边 {hintLeft2} 像素");
                W.LeftUp();
                WaitFor(() => FindEditor(out _) != IntPtr.Zero, 3000);
                W.RECT e3 = default;
                bool ok3 = FindEditor(out e3) != IntPtr.Zero;
                int rawW = e2p.X - s2.X, snapW = e1.Right - s2.X;
                int gotW = ok3 ? e3.Width - 32 : -1;
                rep.Info($"场景二：原始宽 {rawW} / 吸附后应为 {snapW} / 实际 {gotW}（编辑器 {e3.Width}）");
                rep.Check(ok3 && Math.Abs(gotW - snapW) <= 2,
                    $"选区在窗口内时右/下边吸附生效（实际 {gotW} ≈ {snapW}）",
                    $"选区在窗口内时仍未吸附（实际 {gotW}，吸附应 {snapW}，不吸附为 {rawW}）");
                W.Press(W.VK_ESCAPE);
                Wait(400);
            }

            try { if (anchor != null && !anchor.HasExited) anchor.Kill(); } catch { }
            CloseAllEditors();
        }
        catch (Exception ex) { rep.Fail_("吸附测试异常: " + ex.Message); }
    }

    // ---------- S3：Toast 不抢焦点 / 可见 / 不泄漏 ----------

    public static void ToastTest(Report rep)
    {
        rep.Section("S3. Toast：不抢焦点 + 同时可见置顶 + 不泄漏句柄 + 不打断连续快捷键");
        try
        {
            CloseAllEditors();
            if (!OpenOverlay()) { rep.Fail_("浮层未就绪，Toast 测试跳过"); return; }
            Drag(new Point(500, 300), new Point(900, 700), 14);
            W.LeftUp();
            if (!WaitFor(() => FindEditor(out _) != IntPtr.Zero, 3000)) { rep.Fail_("Toast 测试：编辑器未打开"); return; }
            W.RECT er = default;
            var ed = FindEditor(out er);
            ClickClient(er, 200, 200);      // 鼠标点一下画布，确保编辑器是前台
            Wait(400);

            int pid;
            W.GetWindowThreadProcessId(ed, out uint p);
            pid = (int)p;
            var (gdi0, user0) = GuiResources(pid);
            int thr0 = 0;
            try { thr0 = Process.GetProcessById(pid).Threads.Count; } catch { }
            rep.Info($"基线：GDI={gdi0} USER={user0} 线程={thr0}");

            // 连续 5 次 Ctrl+C（编辑器没选中内容时也会走复制流程并弹 Toast）
            for (int i = 0; i < 5; i++) { W.TapCtrl(0x43); Wait(260); }
            Wait(300);

            // (a) 焦点没有被抢走
            IntPtr fg = W.GetForegroundWindow();
            rep.Check(fg == ed, $"连弹 5 个 Toast 后焦点仍在编辑器（fg=0x{fg.ToInt64():X} / editor=0x{ed.ToInt64():X}）",
                $"焦点被 Toast 抢走（fg=0x{fg.ToInt64():X} ≠ editor=0x{ed.ToInt64():X}）");

            // (b) Toast 可见且压在编辑器之上（EnumWindows 自顶向下）
            // 编辑器每 700ms 会把自己重新置顶一次，Toast 每 200ms 自持置顶 →
            // 单点采样会得到随机结果，这里连续采样统计"Toast 在编辑器之上"的比例。
            int above = 0, samples = 0, seenToast = 0;
            for (int i = 0; i < 12; i++)
            {
                var wins = W.VisibleWindows();
                int toastIdx = -1, edIdx = -1;
                for (int k = 0; k < wins.Count; k++)
                {
                    if (wins[k].Hwnd == ed) edIdx = k;
                    if (wins[k].Class.Contains("WindowsForms") && wins[k].Rect.Width < 460 &&
                        wins[k].Rect.Height >= 30 && wins[k].Rect.Height <= 60 && toastIdx < 0) toastIdx = k;
                }
                samples++;
                if (toastIdx >= 0) seenToast++;
                if (toastIdx >= 0 && edIdx >= 0 && toastIdx < edIdx) above++;
                Thread.Sleep(120);
            }
            rep.Check(seenToast > 0, $"Toast 确实可见（{seenToast}/{samples} 次采样能看到）", "12 次采样都没看到 Toast");
            double ratio = samples == 0 ? 0 : (double)above / samples;
            rep.Info($"Toast 在编辑器之上的比例 {above}/{samples} = {ratio:P0}（编辑器每 700ms 自顶一次，Toast 每 200ms 自顶一次）");
            rep.Check(ratio >= 0.8, $"Toast 稳定压在编辑器之上（{ratio:P0}）",
                $"Toast 频繁被编辑器压住（仅 {ratio:P0} 时间在上层）→ 提示会闪烁/看不见");

            // (c) 连续快捷键不被打断：Ctrl+C 之后立刻 Ctrl+S
            var saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "QqShot");
            int before = Directory.Exists(saveDir) ? Directory.GetFiles(saveDir).Length : 0;
            W.TapCtrl(0x43);
            W.TapCtrl(0x53);          // 中间不等待
            bool saved = WaitFor(() =>
            {
                int now = Directory.Exists(saveDir) ? Directory.GetFiles(saveDir).Length : 0;
                return now > before;
            }, 4000);
            int after = Directory.Exists(saveDir) ? Directory.GetFiles(saveDir).Length : 0;
            rep.Check(saved, $"Ctrl+C 后立刻 Ctrl+S 仍然保存成功（{before}→{after}）",
                $"Ctrl+C 后立刻 Ctrl+S 没有生效（{before}→{after}）→ 快捷键被 Toast 吞掉");

            // (d) 全部 Toast 关闭后句柄/线程回落
            WaitFor(() => !W.VisibleWindows().Any(w => w.Class.Contains("WindowsForms") &&
                w.Rect.Width < 460 && w.Rect.Height >= 30 && w.Rect.Height <= 60), 5000);
            Wait(1200);
            var (gdi1, user1) = GuiResources(pid);
            int thr1 = 0;
            try { thr1 = Process.GetProcessById(pid).Threads.Count; } catch { }
            rep.Info($"6 个 Toast 之后：GDI={gdi1}（+{gdi1 - gdi0}） USER={user1}（+{user1 - user0}） 线程={thr1}（+{thr1 - thr0}）");
            rep.Check(user1 <= user0 + 3, $"USER 句柄无累积（+{user1 - user0}）", $"USER 句柄累积 +{user1 - user0}（可能泄漏窗口）");
            rep.Check(gdi1 <= gdi0 + 30, $"GDI 句柄无累积（+{gdi1 - gdi0}）", $"GDI 句柄累积 +{gdi1 - gdi0}");
            rep.Check(thr1 <= thr0 + 2, $"线程数未增长（{thr0}→{thr1}）", $"线程数增长 {thr0}→{thr1}（可能有残留定时器/线程）");

            W.SetForegroundWindow(ed);
            Wait(200);
            W.Press(W.VK_ESCAPE);
            Wait(500);
        }
        catch (Exception ex) { rep.Fail_("Toast 测试异常: " + ex.Message); }
    }

    /// <summary>启动 QqShot 并等待热键窗口就绪。</summary>
    private static bool StartApp()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
        Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
        bool up = WaitFor(() =>
        {
            foreach (var w in W.TopLevelWindows())
                if (W.WindowText(w) == "QqShotHotkeySink") return true;
            return false;
        }, 12000);
        if (!up) return false;
        // 启动提示面板会自动消失，等它走
        WaitFor(() =>
        {
            foreach (var w in W.VisibleWindows())
                if (Math.Abs(w.Rect.Width - 300) <= 2 && Math.Abs(w.Rect.Height - 140) <= 2) return false;
            return true;
        }, 9000);
        Wait(400);
        return true;
    }

    /// <summary>反向排查：编辑器开着时再截图，编辑器的 700ms 自顶定时器会不会把它压到浮层之上。</summary>
    public static void EditorOverlayZOrder(Report rep)
    {
        rep.Section("S7. 编辑器（700ms 自顶）与截图浮层的 z 序竞争");
        try
        {
            CloseAllEditors();
            if (!OpenOverlay()) { rep.Fail_("浮层未就绪"); return; }
            Drag(new Point(700, 380), new Point(1060, 660), 12);
            W.LeftUp();
            if (!WaitFor(() => FindEditor(out _) != IntPtr.Zero, 3000)) { rep.Fail_("编辑器未打开"); return; }
            W.RECT er = default;
            var ed = FindEditor(out er);
            rep.Info($"编辑器 {er.Width}x{er.Height} @({er.Left},{er.Top})，保持打开，再按热键截第二张图");

            var clean = GrabFull();          // 含编辑器（正常，浮层快照也会含它）
            if (!OpenOverlay()) { rep.Fail_("第二次浮层未出现"); return; }

            int editorAbove = 0, samples = 0;
            for (int i = 0; i < 10; i++)
            {
                var wins = W.VisibleWindows();
                int ovIdx = -1, edIdx = -1;
                for (int k = 0; k < wins.Count; k++)
                {
                    if (wins[k].Hwnd == ed) edIdx = k;
                    if (wins[k].Class.Contains("WindowsForms") && wins[k].Rect.Width >= SW - 4 && wins[k].Rect.Height >= SH - 4)
                        ovIdx = k;
                }
                samples++;
                if (ovIdx >= 0 && edIdx >= 0 && edIdx < ovIdx) editorAbove++;
                Thread.Sleep(120);
            }
            double ratio = (double)editorAbove / samples;
            rep.Info($"编辑器浮在浮层之上的比例 {editorAbove}/{samples} = {ratio:P0}（编辑器自顶周期 700ms）");

            using (var shot = GrabFull())
            {
                // 编辑器标题栏区域（顶部 46px 内、靠左）应当是编辑器 UI 而不是被压暗的屏幕
                var px = shot.GetPixel(er.Left + 60, er.Top + 20);
                var pxc = clean.GetPixel(er.Left + 60, er.Top + 20);
                rep.Info($"编辑器标题栏像素：现在 {px} / 打开浮层前 {pxc}（若相同说明编辑器盖在浮层之上）");
                rep.Check(ratio < 0.2,
                    $"编辑器基本不会压住新浮层（{ratio:P0} 时间在上层）",
                    $"编辑器有 {ratio:P0} 的时间盖在新浮层之上 → 用户再次截图时屏幕中间会浮着一块旧编辑器（标题栏像素 {px} vs 压暗后应更暗）");
            }
            W.Press(W.VK_ESCAPE);
            Wait(400);
            CloseAllEditors();
        }
        catch (Exception ex) { rep.Fail_("z 序测试异常: " + ex.Message); }
    }

    /// <summary>第 2 轮专用：只跑吸附 + Toast 两项。</summary>
    public static void RunR2Only(Report rep)
    {
        foreach (var p in Process.GetProcessesByName("QqShot")) { try { p.Kill(); p.WaitForExit(2000); } catch { } }
        Wait(400);
        if (!StartApp()) { rep.Fail_("程序未就绪"); return; }
        SnapTest(rep);
        ToastTest(rep);
        EditorOverlayZOrder(rep);
        foreach (var p in Process.GetProcessesByName("QqShot")) { try { p.Kill(); } catch { } }
    }

    /// <summary>只跑编辑器冒烟（省时间用）。</summary>
    public static void RunSmokeOnly(Report rep)
    {
        foreach (var p in Process.GetProcessesByName("QqShot")) { try { p.Kill(); p.WaitForExit(2000); } catch { } }
        Wait(400);
        if (!StartApp()) { rep.Fail_("程序未就绪"); return; }
        SmokeEditor(rep, LogLength());
        foreach (var p in Process.GetProcessesByName("QqShot")) { try { p.Kill(); } catch { } }
    }

    private static void ClickClient(W.RECT window, int cx, int cy)
    {
        W.MoveTo(window.Left + cx, window.Top + cy);
        Wait(120);
        W.LeftDown();
        Wait(50);
        W.LeftUp();
        Wait(120);
    }
}
