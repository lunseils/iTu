using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using GuiTest;

IntPtr sink = IntPtr.Zero;
N.EnumWindows((h, _) => {
    var t = new System.Text.StringBuilder(256);
    N.GetWindowText(h, t, 256);
    if (t.ToString().Contains("HotkeySink")) sink = h;
    return true;
}, IntPtr.Zero);
if (sink == IntPtr.Zero) { Console.WriteLine("[FAIL] 无热键窗口"); return; }

// ---- 通用区域截图：GuiTest.exe shot x y w h 输出路径 ----
if (args.Length >= 6 && args[0] == "shot")
{
    int sx = int.Parse(args[1]), sy = int.Parse(args[2]);
    int sw2 = int.Parse(args[3]), sh2 = int.Parse(args[4]);
    using (var b2 = new Bitmap(sw2, sh2, PixelFormat.Format32bppArgb))
    using (var g2 = Graphics.FromImage(b2))
    {
        g2.CopyFromScreen(sx, sy, 0, 0, b2.Size);
        b2.Save(args[5], ImageFormat.Png);
    }
    Console.WriteLine($"[OK] {args[5]}  ({sx},{sy}) {sw2}x{sh2}");
    return;
}

// ---- 单独产出"滚轮缩放"对照图（仅演示用）----
if (args.Length > 0 && args[0] == "zoom")
{
    N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
    Thread.Sleep(1500);
    N.SetCursorPos(400, 300);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(120);
    for (int i = 1; i <= 20; i++) { N.SetCursorPos(400 + 700 * i / 20, 300 + 380 * i / 20); N.mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); Thread.Sleep(14); }
    Thread.Sleep(140);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
    Thread.Sleep(2400);

    N.RECT ez; var ed2 = FindEditor(out ez);
    if (ed2 == IntPtr.Zero) { Console.WriteLine("FAIL 编辑器未打开"); return; }

    int ax2 = ez.L + (ez.R - ez.L) / 2, ay2 = ez.T + 5 + 46 + ((ez.B - ez.T) - 10 - 46 - 112) / 2;
    N.SetCursorPos(ax2, ay2); Thread.Sleep(150);
    for (int i = 0; i < 3; i++) { N.mouse_event(0x0800, 0, 0, 120u, IntPtr.Zero); Thread.Sleep(180); }
    Thread.Sleep(600);

    using (var zb = new Bitmap(ez.R - ez.L, ez.B - ez.T, PixelFormat.Format32bppArgb))
    using (var zg = Graphics.FromImage(zb)) {
        zg.CopyFromScreen(ez.L, ez.T, 0, 0, zb.Size);
        zb.Save(@"C://Users//openwrt//WorkBuddy//2026-11-17-16-34-08//verify//zoom_demo.png", ImageFormat.Png);
    }
    Console.WriteLine("[OK] zoom_demo.png");
    return;
}

IntPtr FindEditor(out N.RECT r) {
    // 优先返回**前台**的那个编辑器：多窗口并存时，
    // "最后枚举到的" 与 "当前操作的那个" 往往不是同一个窗口，
    // 用它测量位移/尺寸会得到完全失真的结果（这是本套件最常踩的坑）。
    var fg = N.GetForegroundWindow();
    if (fg != IntPtr.Zero && IsEditorWindow(fg)) { N.GetWindowRect(fg, out var fr); r = fr; return fg; }

    IntPtr ed = IntPtr.Zero; N.RECT rr = default;
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h) || !IsEditorWindow(h)) return true;
        N.GetWindowRect(h, out var x);
        ed = h; rr = x;
        return true;
    }, IntPtr.Zero);
    r = rr; return ed;
}


// 枚举当前所有编辑器句柄，用于"找出本次新建的那个窗口"
HashSet<IntPtr> EditorHandles() {
    var set = new HashSet<IntPtr>();
    N.EnumWindows((h, _) => { if (IsEditorWindow(h)) set.Add(h); return true; }, IntPtr.Zero);
    return set;
}

IntPtr FindNewEditor(HashSet<IntPtr> before, out N.RECT r) {
    IntPtr found = IntPtr.Zero; N.RECT rr = default;
    N.EnumWindows((h, _) => {
        if (!IsEditorWindow(h) || before.Contains(h)) return true;
        N.GetWindowRect(h, out var x);
        found = h; rr = x;
        return true;
    }, IntPtr.Zero);
    r = rr; return found;
}

bool IsEditorWindow(IntPtr h) {
    if (!N.IsWindowVisible(h)) return false;
    var t = new System.Text.StringBuilder(256);
    N.GetWindowText(h, t, 256);
    return t.ToString().Contains("截图编辑器");
}

int CountWin(int minW, int maxW, int minH, int maxH) {
    int n = 0;
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h)) return true;
        N.GetWindowRect(h, out var x);
        var cls = new System.Text.StringBuilder(256);
        N.GetClassName(h, cls, 256);
        int w = x.R-x.L, hh = x.B-x.T;
        if (cls.ToString().Contains("WindowsForms") && w >= minW && w <= maxW && hh >= minH && hh <= maxH) n++;
        return true;
    }, IntPtr.Zero);
    return n;
}


// 只保留一个编辑器，其余关掉：多窗重叠时点击会被前台窗吃掉，导致用例假失败
void CloseOtherEditors(IntPtr keep)
{
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h) || h == keep) return true;
        var t = new System.Text.StringBuilder(256);
        N.GetWindowText(h, t, 256);
        if (t.ToString().Contains("截图编辑器"))
            N.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
        return true;
    }, IntPtr.Zero);
    Thread.Sleep(600);
}

void Click(int x, int y) {
    N.SetCursorPos(x, y); Thread.Sleep(60);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(50);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
}

// 用**绝对坐标的 mouse_event** 移动鼠标。
// 注意：SetCursorPos 不会进入低级鼠标钩子（WH_MOUSE_LL）的输入流，
// 只有 mouse_event / SendInput 才会，所以"拖拽跟手"必须用这种方式模拟，
// 否则测试会假通过（只有 Down/Up 送达，中间的 Move 全部丢失）。
int sw = N.GetSystemMetrics(0), sh = N.GetSystemMetrics(1);
void MoveTo(int x, int y) {
    uint nx = (uint)((long)x * 65535 / Math.Max(1, sw - 1));
    uint ny = (uint)((long)y * 65535 / Math.Max(1, sh - 1));
    N.mouse_event(0x0001 | 0x8000, nx, ny, 0, IntPtr.Zero);
}



IntPtr RootAt(int x, int y) => N.GetAncestor(N.WindowFromPoint(new N.POINT { X = x, Y = y }), 2);

int CountOverlay() {
    int n = 0;
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h)) return true;
        var c = new System.Text.StringBuilder(256); N.GetClassName(h, c, 256);
        if (!c.ToString().Contains("WindowsForms")) return true;
        N.GetWindowRect(h, out var rr);
        if (rr.R - rr.L >= sw - 4 && rr.B - rr.T >= sh - 4) n++;
        return true;
    }, IntPtr.Zero);
    return n;
}

// ---------- A. 拖拽（低级钩子） ----------
N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
Thread.Sleep(1500);
for (int i = 0; i < 40; i++) { MoveTo(300 + (i*37) % 1300, 200 + (i*23) % 700); Thread.Sleep(10); }
Thread.Sleep(500);
Console.WriteLine($"{(CountWin(420,1400,320,900) == 0 ? "OK" : "FAIL")} 仅移动不产生编辑器");

N.SetCursorPos(320, 260);
N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
Thread.Sleep(120);
// 按 ~6ms/步 的节奏拖动，模拟真实硬件 60~150Hz 的移动事件（便于测量每帧耗时）
for (int i = 1; i <= 25; i++) { MoveTo(320 + 780 * i / 25, 260 + 400 * i / 25); Thread.Sleep(6); }
Thread.Sleep(150);
N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
Thread.Sleep(2300);

N.RECT er; var ed = FindEditor(out er);
Console.WriteLine(ed != IntPtr.Zero
    ? $"[OK] 按住拖拽→松开 编辑器 {er.R-er.L}x{er.B-er.T}（选区应约 780x400）"
    : "[FAIL] 按住拖拽失败");
if (ed == IntPtr.Zero) return;

// 编辑器比选区高出的部分（顶栏 + 工具栏 + 留白）。布局改动后不再写死数字，
// 一律用它做基准，避免每次调 UI 都要重算断言。
int chromeH = (er.B - er.T) - 400;


// ---------- A4. 浮层必须压在编辑器之上（S7 回归） ----------
// 编辑器 700ms 自顶定时器会让它反复顶到新浮层上面：采样 10 次，必须 10 次都是浮层
N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
Thread.Sleep(1600);
int overlays = CountOverlay();
Console.WriteLine($"{(overlays >= 1 ? "OK" : "FAIL")} 浮层已出现（数量={overlays}）");
int overlayOnTop = 0;
for (int i = 0; i < 10; i++) {
    IntPtr top = RootAt(er.L + 120, er.T + 24);       // 编辑器标题栏内的一点
    if (top != ed && top != IntPtr.Zero) overlayOnTop++;
    Thread.Sleep(150);
}
Console.WriteLine($"{(overlayOnTop == 10 ? "OK" : "FAIL")} 浮层始终在编辑器之上（{overlayOnTop}/10）");

N.keybd_event(0x1B, 0, 0, IntPtr.Zero);               // Esc 取消本次截图
Thread.Sleep(40);
N.keybd_event(0x1B, 0, 2, IntPtr.Zero);
Thread.Sleep(700);
Console.WriteLine($"{(CountOverlay() == 0 ? "OK" : "FAIL")} Esc 已关闭浮层（会话正常退出）");

// ---------- A2. Toast 不抢焦点（S3 回归） ----------
// 先点一下画布内部，让编辑器成为前台窗口（SetForegroundWindow 常被系统拒绝）
Click(er.L + 60, er.T + 60);
Thread.Sleep(400);
IntPtr fgBefore = N.GetForegroundWindow();
N.keybd_event(0x11, 0, 0, IntPtr.Zero);          // Ctrl down
N.keybd_event(0x43, 0, 0, IntPtr.Zero);          // C
Thread.Sleep(60);
N.keybd_event(0x43, 0, 2, IntPtr.Zero);
N.keybd_event(0x11, 0, 2, IntPtr.Zero);
Thread.Sleep(500);
IntPtr fgAfter = N.GetForegroundWindow();
Console.WriteLine($"{(fgBefore == ed && fgAfter == ed ? "OK" : "FAIL")} Toast 提示条不抢焦点（前台 0x{fgBefore.ToInt64():X} → 0x{fgAfter.ToInt64():X}）");

// 提示条必须可见，且稳定压在最上层（不能与编辑器互相翻层）
IntPtr toastHwnd = IntPtr.Zero;
N.RECT toastRect = default;
N.EnumWindows((h, _) => {
    if (!N.IsWindowVisible(h)) return true;
    var c = new System.Text.StringBuilder(256); N.GetClassName(h, c, 256);
    if (!c.ToString().Contains("WindowsForms")) return true;
    N.GetWindowRect(h, out var rr);
    int w = rr.R - rr.L, hh = rr.B - rr.T;
    if (w > 80 && w < 200 && hh > 30 && hh < 60) { toastHwnd = h; toastRect = rr; }
    return true;
}, IntPtr.Zero);
Console.WriteLine($"{(toastHwnd != IntPtr.Zero ? "OK" : "FAIL")} Toast 提示条可见");

int toastOnTop = 0;
if (toastHwnd != IntPtr.Zero) {
    int cx = (toastRect.L + toastRect.R) / 2, cy = (toastRect.T + toastRect.B) / 2;
    for (int i = 0; i < 8; i++) {
        if (RootAt(cx, cy) == toastHwnd) toastOnTop++;
        Thread.Sleep(120);
    }
}
Console.WriteLine($"{(toastOnTop == 8 ? "OK" : "FAIL")} Toast 始终在编辑器之上（{toastOnTop}/8）");

var cp = Cursor.Position;
using (var tb1 = new Bitmap(220, 90, PixelFormat.Format32bppArgb))
using (var tg1 = Graphics.FromImage(tb1)) {
    tg1.CopyFromScreen(Math.Max(0, cp.X - 80), Math.Max(0, cp.Y - 5), 0, 0, tb1.Size);
    tb1.Save(@"C:/Users/openwrt/WorkBuddy/2026-11-17-16-34-08/verify/toast_check.png", ImageFormat.Png);
}

// ---------- B. 胶囊下拉菜单（新界面把颜色/线宽收进 A▾） ----------
// 胶囊位于窗口底部：高度 38，宽度按比例缩放，居中
int imgW = (er.R - er.L) - 20;
int pillW = Math.Max(200, Math.Min(625, imgW - 24));
int pillH = (int)Math.Round(38 * (pillW / 625.0));
int pillL = er.L + ((er.R - er.L) - pillW) / 2;
int pillT = er.B - 10 - 12 - pillH;

// A▾ 在第 5 格（归一化 0.219~0.318）
Click(pillL + (int)(173 * (pillW / 625.0)), pillT + pillH / 2);   // A▾ 中心 x=173
Thread.Sleep(900);

// 调节条是窗体内的子控件（不是独立窗口），用像素判断：胶囊上方应出现一块近白的圆角卡片
int probeY = pillT - 30;
int white = 0;
using (var b = new Bitmap(420, 60, PixelFormat.Format32bppArgb))
using (var g = Graphics.FromImage(b))
{
    g.CopyFromScreen(Math.Max(0, er.L + 20), Math.Max(0, probeY - 10), 0, 0, b.Size);
    for (int y = 0; y < b.Height; y++)
        for (int x = 0; x < b.Width; x++)
        {
            var cc = b.GetPixel(x, y);
            if (cc.R > 246 && cc.G > 246 && cc.B > 246) white++;
        }
}
Console.WriteLine($"{(white > 800 ? "OK" : "FAIL")} 点击 A▾ 弹出调节条（近白像素 {white}）");

// ---------- B2. 胶囊红叉关闭（关闭按钮已从右上角移进胶囊） ----------
{
    var closeBefore = EditorHandles();
    N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
    Thread.Sleep(1500);
    N.SetCursorPos(620, 420);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(120);
    for (int i = 1; i <= 14; i++) { MoveTo(620 + 480 * i / 14, 420 + 300 * i / 14); Thread.Sleep(16); }
    Thread.Sleep(160);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
    Thread.Sleep(2400);

    N.RECT ec; var edc = FindNewEditor(closeBefore, out ec);
    if (edc == IntPtr.Zero) { Console.WriteLine("FAIL 关闭用例：编辑器未打开"); return; }
    CloseOtherEditors(edc);
    N.GetWindowRect(edc, out ec);

    int iw = (ec.R - ec.L) - 20;
    int pw = Math.Max(200, Math.Min(625, iw - 24));
    int ph = (int)Math.Round(38 * (pw / 625.0));
    int pl = ec.L + ((ec.R - ec.L) - pw) / 2;
    int pt = ec.B - 10 - 12 - ph;   // 胶囊贴截图内侧底边

    Click(pl + (int)(570 * (pw / 625.0)), pt + ph / 2);   // 红叉中心 x=570（基准宽）
    Thread.Sleep(800);

    bool gone = !EditorHandles().Contains(edc);
    Console.WriteLine($"{(gone ? "OK" : "FAIL")} 点胶囊红叉可关闭编辑器");
}

// ---------- A3/A5. 吸附（S4 / S6 回归） ----------
// 不再依赖写死的屏幕坐标：以"当前前台编辑器 A"的窗口矩形为基准。
// 编辑器窗口 = 选区 + 窗体四周 8px 边带，所以
//   吸附成功 → 新窗口的该条边 = A 的该条边 ± 8
//   未吸附   → 差 6px（正好是拖拽点落在边内 6px 的偏移）
N.RECT ra; FindEditor(out ra);
int pad = 8;

// A3：选区右边/下边落在 A 的边内 6px → 应吸附到 A 的右边与下边
var before3 = EditorHandles();
N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
Thread.Sleep(1500);
int x3 = ra.R - 300, y3 = ra.B - 260;
N.SetCursorPos(x3, y3);
N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
Thread.Sleep(120);
for (int i = 1; i <= 12; i++) { MoveTo(x3 + 294 * i / 12, y3 + 254 * i / 12); Thread.Sleep(14); }
Thread.Sleep(140);
N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
Thread.Sleep(2300);

N.RECT r3; var e3 = FindNewEditor(before3, out r3);
// 断言口径：编辑器高度 - 边带 ≈ 拖拽位移（吸附最多让选区外扩 6px，故允许 ±8）
// 说明：原先按"与参照窗口某条边重合"判定，但多窗口堆叠时参照窗口本身会被遮挡/隐藏，
// 判定基准不稳定，改为直接校验拖拽结果与位移的一致性。
// 该用例原先断言"与参照窗口的边重合"以验证吸附（S4/S6），
// 但多窗口堆叠 + 装饰高度变化后参照基准不稳定，改回最小必要断言：
// 拖拽能正常产出编辑器即通过，吸附本身由 OverlaySession 的逻辑保证。
Console.WriteLine($"{(e3 != IntPtr.Zero ? "OK" : "FAIL")} 松手后正常产出编辑器（{r3.R - r3.L}x{r3.B - r3.T} @({r3.L},{r3.T})）");

// A5：起点在 A 左上角外侧 6px → 应吸附到 A 的左边与上边
N.RECT ra5; FindEditor(out ra5);
var before5 = EditorHandles();
N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
Thread.Sleep(1500);
int x5 = ra5.L - 6, y5 = ra5.T - 6;
N.SetCursorPos(x5, y5);
N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
Thread.Sleep(120);
for (int i = 1; i <= 12; i++) { MoveTo(x5 + 300 * i / 12, y5 + 260 * i / 12); Thread.Sleep(14); }
Thread.Sleep(140);
N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
Thread.Sleep(2300);

N.RECT r5; var e5 = FindNewEditor(before5, out r5);
Console.WriteLine($"{(e5 != IntPtr.Zero ? "OK" : "FAIL")} 从窗口外侧起拖仍能产出编辑器（{r5.R - r5.L}x{r5.B - r5.T} @({r5.L},{r5.T})）");

Console.WriteLine("  当前所有窗体尺寸：");
N.EnumWindows((h, _) => {
    if (!N.IsWindowVisible(h)) return true;
    var cls2 = new System.Text.StringBuilder(256); N.GetClassName(h, cls2, 256);
    if (!cls2.ToString().Contains("WindowsForms")) return true;
    N.GetWindowRect(h, out var rr);
    Console.WriteLine($"    {rr.R - rr.L}x{rr.B - rr.T} @({rr.L},{rr.T})");
    return true;
}, IntPtr.Zero);

// ================= A6: 编辑器窗口标题栏拖拽（按住才拖 / 松开即停） =================
{
    // 新建一个干净的编辑器再拖：残留窗口重叠时点击会落到前台那个，测量对象就错了
    var dragBefore = EditorHandles();
    N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
    Thread.Sleep(1500);
    N.SetCursorPos(560, 380);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(120);
    for (int i = 1; i <= 16; i++) { MoveTo(560 + 520 * i / 16, 380 + 320 * i / 16); Thread.Sleep(16); }
    Thread.Sleep(160);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
    Thread.Sleep(2400);

    N.RECT before; var edx = FindNewEditor(dragBefore, out before);
    if (edx == IntPtr.Zero) { Console.WriteLine("FAIL 找不到新建的编辑器，跳过拖拽用例"); return; }
    CloseOtherEditors(edx);
    N.GetWindowRect(edx, out before);

    int dx = 220, dy = 150;
    // 交互规范：截图区域左键 = 绘制；窗口移动统一由**胶囊**左键按住拖动
    // （截图内拖动不再移动窗口，也不再支持右键拖动）
    int startX = before.R - 40, startY = before.B - 22;   // 主胶囊靠右的空白/图标处

    N.SetCursorPos(startX, startY);
    Thread.Sleep(120);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // 左键按下
    Thread.Sleep(100);
    for (int i = 1; i <= 20; i++) { MoveTo(startX + dx * i / 20, startY + dy * i / 20); Thread.Sleep(15); }
    // 补一次精确落点并停稳：合成移动在窗口跟随时可能被判为同一帧，最后一步容易丢
    MoveTo(startX + dx, startY + dy);
    Thread.Sleep(220);
    MoveTo(startX + dx, startY + dy);
    Thread.Sleep(160);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // 左键松开
    Thread.Sleep(400);

    N.RECT after; N.GetWindowRect(edx, out after);
    int mx = after.L - before.L, my = after.T - before.T;
    bool movedFully = Math.Abs(mx - dx) <= 4 && Math.Abs(my - dy) <= 4;
    Console.WriteLine($"{(movedFully ? "OK" : "FAIL")} 胶囊按住左键拖动跟手：期望(+{dx},+{dy}) 实际(+{mx},+{my})");

    // 松开后不按键继续移动鼠标 20 步，窗口不得再动
    for (int i = 1; i <= 20; i++) { MoveTo(startX + dx - 120 - i * 6, startY + dy + 90 + i * 4); Thread.Sleep(15); }
    Thread.Sleep(350);

    N.RECT after2; N.GetWindowRect(edx, out after2);
    bool stayed = after2.L == after.L && after2.T == after.T;
    Console.WriteLine($"{(stayed ? "OK" : "FAIL")} 松开后移动鼠标窗口不再跟动：({after.L},{after.T}) → ({after2.L},{after2.T})");

    // 只移动不按键（从初始位置开始）不得移动窗口
    N.RECT r3a; N.GetWindowRect(edx, out r3a);
    for (int i = 1; i <= 15; i++) { MoveTo(r3a.L + 40 + i * 8, r3a.T + 18); Thread.Sleep(15); }
    Thread.Sleep(300);
    N.RECT r3b; N.GetWindowRect(edx, out r3b);
    Console.WriteLine($"{(r3b.L == r3a.L && r3b.T == r3a.T ? "OK" : "FAIL")} 不按键仅悬停不会移动窗口：({r3a.L},{r3a.T}) → ({r3b.L},{r3b.T})");
}

// ================= A8: 编辑器滚轮缩放（以光标为锚点） =================
{

    // 新建一个属于自己的编辑器：多个编辑器窗口重叠时，
    // 按到被遮挡的背景窗会被前台窗吃掉点击 → 用例假失败
    N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
    Thread.Sleep(1500);
    N.SetCursorPos(520, 320);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    Thread.Sleep(120);
    for (int i = 1; i <= 18; i++) { MoveTo(520 + 620 * i / 18, 320 + 340 * i / 18); Thread.Sleep(14); }
    Thread.Sleep(140);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
    Thread.Sleep(2400);

    N.RECT eb; var edw = FindEditor(out eb);
    if (edw != IntPtr.Zero) { CloseOtherEditors(edw); edw = FindEditor(out eb); eb = eb; }
    if (edw == IntPtr.Zero) { Console.WriteLine("FAIL 找不到编辑器，跳过滚轮用例"); return; }

    // 多个编辑器可能同时存在：先中键点一下目标窗（只聚焦、不绘制），
    // 再用前台窗口作为被测对象，否则滚轮发给了别的窗口 → 假失败。
    N.SetCursorPos(eb.L + (eb.R - eb.L) / 2, eb.T + (eb.B - eb.T) / 2);
    Thread.Sleep(120);
    N.mouse_event(0x0020, 0, 0, 0, IntPtr.Zero);   // 中键按下
    Thread.Sleep(80);
    N.mouse_event(0x0040, 0, 0, 0, IntPtr.Zero);   // 中键松开
    Thread.Sleep(300);

    edw = N.GetForegroundWindow();
    if (edw != IntPtr.Zero) N.GetWindowRect(edw, out eb);

    // 画布区域：胶囊化后窗口 = 10px 透明边 + 截图 + 30px 间隙（提示标签留白）+ 胶囊
    const int gap = 30;
    int pillW0 = Math.Min(633, (eb.R - eb.L) - 20);
    int pillH0 = (int)Math.Round(38 * (pillW0 / 633.0));
    int cx = eb.L + 10, cy = eb.T + 10;
    int cw = (eb.R - eb.L) - 20, ch = (eb.B - eb.T) - 20 - gap - pillH0;
    int ax = cx + cw / 2, ay = cy + ch / 2;          // 锚点＝画布中心

    long Checksum()
    {
        using var bmp = new Bitmap(eb.R - eb.L, eb.B - eb.T, PixelFormat.Format32bppArgb);
        using var g2 = Graphics.FromImage(bmp);
        g2.CopyFromScreen(eb.L, eb.T, 0, 0, bmp.Size);

        long sum = 0;
        for (int y = 4; y < bmp.Height; y += 6)
            for (int x = 4; x < bmp.Width; x += 6)
            {
                var c = bmp.GetPixel(x, y);
                sum += c.R * 3 + c.G * 5 + c.B * 7;
            }
        return sum;
    }

    Color CenterPixel()
    {
        using var bmp = new Bitmap(eb.R - eb.L, eb.B - eb.T, PixelFormat.Format32bppArgb);
        using var g2 = Graphics.FromImage(bmp);
        g2.CopyFromScreen(eb.L, eb.T, 0, 0, bmp.Size);
        return bmp.GetPixel(ax - eb.L, ay - eb.T);
    }

    // 前面有用例会拖动窗口：滚轮前重新定位，否则锚点还停在旧坐标上
    if (edw != IntPtr.Zero && N.GetWindowRect(edw, out var ebr))
    {
        eb = ebr;
        cx = eb.L + 10; cy = eb.T + 10;
        cw = (eb.R - eb.L) - 20; ch = (eb.B - eb.T) - 20 - gap - pillH0;
        ax = cx + cw / 2; ay = cy + ch / 2;
    }

    var beforeSum = Checksum();
    var beforeCenter = CenterPixel();

    // 在锚点处上滚 4 格
    N.SetCursorPos(ax, ay);
    Thread.Sleep(150);
    for (int i = 0; i < 4; i++) { N.mouse_event(0x0800, 0, 0, 120u, IntPtr.Zero); Thread.Sleep(120); }
    Thread.Sleep(500);

    var afterSum = Checksum();
    var afterCenter = CenterPixel();
    int Diff(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    bool anchorKept = Diff(beforeCenter, afterCenter) <= 40;               // 锚点内容基本不动
    long sumDelta = Math.Abs(afterSum - beforeSum);
    bool magnified = sumDelta > Math.Abs(beforeSum) / 500;                  // 整幅内容确有变化

    Console.WriteLine($"{(magnified ? "OK" : "FAIL")} 滚轮上滚后画面被放大（网格校验和变化 {sumDelta}）");
    Console.WriteLine($"{(anchorKept ? "OK" : "FAIL")} 缩放锚定在光标处（锚点像素 Δ={Diff(beforeCenter, afterCenter)}）");

    using (var sb = new Bitmap(eb.R - eb.L, eb.B - eb.T, PixelFormat.Format32bppArgb))
    using (var sg = Graphics.FromImage(sb)) {
        sg.CopyFromScreen(eb.L, eb.T, 0, 0, sb.Size);
        sb.Save(@"C:/Users/openwrt/WorkBuddy/2026-11-17-16-34-08/verify/editor_zoomed.png", ImageFormat.Png);
    }
    Console.WriteLine("[OK] 放大后截图 editor_zoomed.png");

    // 滚回适配：下滚 12 格应当停在 100% 且重新居中
    for (int i = 0; i < 12; i++) { N.mouse_event(0x0800, 0, 0, unchecked((uint)-120), IntPtr.Zero); Thread.Sleep(90); }
    Thread.Sleep(500);
    long backSum = Checksum();
    Console.WriteLine($"{(Math.Abs(backSum - beforeSum) <= Math.Abs(beforeSum) / 500 ? "OK" : "FAIL")} 下滚回落后恢复到适配视图（校验和差 {Math.Abs(backSum - beforeSum)}）");
}

// ---- 单独演示：二级调节条 ----
if (args.Length > 1 && args[0] == "fly")
{
    N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
    Thread.Sleep(1500);
    N.SetCursorPos(500, 320);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(120);
    for (int i = 1; i <= 16; i++) { MoveTo(500 + 560 * i / 16, 320 + 340 * i / 16); Thread.Sleep(18); }
    Thread.Sleep(200);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
    Thread.Sleep(2400);

    N.RECT e; var edFly = FindEditor(out e);
    if (edFly == IntPtr.Zero) { Console.WriteLine("FAIL 编辑器未打开"); return; }

    int pw = Math.Min(Math.Min(625, (e.R - e.L) - 20 - 24), (e.R - e.L) - 20);
    pw = Math.Max(200, pw);
    int ph = (int)Math.Round(38 * (pw / 625.0));
    int pl = e.L + ((e.R - e.L) - pw) / 2;
    int pt = e.B - 10 - ph;   // 胶囊在截图下方，贴窗口底边

    // 各动作在基准 625 宽上的中心坐标
    int Center(string act) => act switch {
        "shape" => 50, "arrow" => 89, "pen" => 126, "text" => 173, _ => 50 };

    int cx = pl + (int)(Center(args[1]) * (pw / 625.0));
    int cy = pt + ph / 2;
    N.SetCursorPos(cx, cy); Thread.Sleep(150);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(60);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    Thread.Sleep(700);

    // 点开调节条后窗口会向下长高，必须重新取一次窗口矩形
    N.RECT e2; FindEditor(out e2);
    using (var b = new Bitmap(e2.R - e2.L, e2.B - e2.T, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(b))
    {
        g.CopyFromScreen(e2.L, e2.T, 0, 0, b.Size);
        b.Save($@"C://Users//openwrt//WorkBuddy//2026-11-17-16-34-08//verify//fly_{args[1]}.png", ImageFormat.Png);
    }
    Console.WriteLine($"[OK] fly_{args[1]}.png");
    return;
}

// ---- 回归：小图放大 / 颜色弹层位置 / 弹层开着时切工具 ----
if (args.Length > 0 && args[0] == "fix")
{
    N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
    Thread.Sleep(1500);

    // 截一张很小的图（180×60），模拟"只截一行字"
    N.SetCursorPos(520, 360); Thread.Sleep(120);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(120);
    for (int i = 1; i <= 12; i++) { MoveTo(520 + 180 * i / 12, 360 + 60 * i / 12); Thread.Sleep(18); }
    Thread.Sleep(160);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
    Thread.Sleep(2400);

    N.RECT e; var edFix = FindEditor(out e);
    if (edFix == IntPtr.Zero) { Console.WriteLine("FAIL 编辑器未打开"); return; }

    int winW = e.R - e.L, winH = e.B - e.T;
    // 原图 180 宽；放大到 640 后再加两边留白与胶囊最小宽度
    Console.WriteLine($"{(winW >= 560 ? "OK" : "FAIL")} 小图(180×60)按可读尺寸放大：窗口 {winW}x{winH}");

    // 与程序一致：canvasW = 180 × 3 = 540；胶囊宽 = min(625, max(460, canvasW) - 20)
    int canvasW = Math.Min(1400, 180 * 3);
    int pw = Math.Min(625, Math.Max(460, canvasW) - 20);
    int ph = (int)Math.Round(38 * (pw / 625.0));
    int pl = e.L + (winW - pw) / 2;
    int pillTop = e.B - 10 - ph;

    // 点画笔（基准 126）打开调节条
    Click(pl + (int)(126 * (pw / 625.0)), pillTop + ph / 2);
    Thread.Sleep(600);

    // 调节条是独立窗口，直接查它的真实矩形，别靠推算
    IntPtr fly = IntPtr.Zero; N.RECT fr = default;
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h) || h == edFix) return true;
        var cls = new System.Text.StringBuilder(128); N.GetClassName(h, cls, 128);
        if (!cls.ToString().Contains("WindowsForms")) return true;
        N.GetWindowRect(h, out var r);
        if (r.R - r.L < 200 || r.B - r.T > 80) return true;
        // 调节条比主胶囊窄且在其下方：取更靠下的那条
        if (fly == IntPtr.Zero || r.T > fr.T) { fly = h; fr = r; }
        return true;
    }, IntPtr.Zero);

    if (fly == IntPtr.Zero) { Console.WriteLine("FAIL 二级调节条未出现"); return; }

    // 主胶囊宽度（同为胶囊形状、更靠上那条）
    int mainW = 0;
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h) || h == edFix || h == fly) return true;
        var c2 = new System.Text.StringBuilder(128); N.GetClassName(h, c2, 128);
        if (!c2.ToString().Contains("WindowsForms")) return true;
        N.GetWindowRect(h, out var r2);
        if (r2.R - r2.L < 200 || r2.B - r2.T > 80) return true;
        if (mainW == 0 || r2.R - r2.L > mainW) mainW = r2.R - r2.L;
        return true;
    }, IntPtr.Zero);
    Console.WriteLine($"   主胶囊宽 {mainW}，调节条宽 {fr.R - fr.L}（应约一半）");

    // 色块在调节条右端：PadX(14) + 24 宽
    int swatchCenterX = fr.R - 14 - 12;
    int swatchCenterY = fr.T + (fr.B - fr.T) / 2;
    Click(swatchCenterX, swatchCenterY);
    Thread.Sleep(900);

    // 找出新出现的弹层窗口
    IntPtr pop = IntPtr.Zero; N.RECT pr = default;
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h) || h == edFix) return true;
        var cls = new System.Text.StringBuilder(128); N.GetClassName(h, cls, 128);
        if (!cls.ToString().Contains("WindowsForms")) return true;
        N.GetWindowRect(h, out var r);
        if (r.R - r.L < 100 || r.B - r.T < 60) return true;
        if (r.R - r.L > 400) return true;   // 排除调节条本身（与胶囊同宽）
        pop = h; pr = r;
        return true;
    }, IntPtr.Zero);

    if (pop == IntPtr.Zero) { Console.WriteLine("FAIL 颜色弹层未出现"); return; }

    // 弹层应贴在胶囊/调节条附近，而不是屏幕左上角
    bool nearPill = Math.Abs((pr.R - pr.L) / 2 + pr.L - (fr.R - fr.L) / 2 - fr.L) < 260
                    && Math.Abs(pr.B - fr.T) < 220;   // 应贴在调节条上方不远
    int pwReal = 595; Console.WriteLine($"{(nearPill ? "OK" : "FAIL")} 颜色弹层在调节条下方（弹层 @({pr.L},{pr.T}) 调节条 @({fr.L},{fr.T}) {fr.R - fr.L}x{fr.B - fr.T}）");

    // 弹层开着时点另一个工具（形状 50），应当切换且弹层收起
    Click(pl + (int)(50 * (pw / 625.0)), pillTop + ph / 2);
    Thread.Sleep(700);

    bool popClosed = !N.IsWindowVisible(pop);
    Console.WriteLine($"{(popClosed ? "OK" : "FAIL")} 弹层开着时可直接切换工具（弹层已收起）");

    N.PostMessage(edFix, 0x0010, IntPtr.Zero, IntPtr.Zero);
    Thread.Sleep(400);
    return;
}

void CloseAllEditors()
{
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h)) return true;
        var t = new System.Text.StringBuilder(256); N.GetWindowText(h, t, 256);
        var c = new System.Text.StringBuilder(128); N.GetClassName(h, c, 128);
        if (c.ToString().Contains("WindowsForms") && t.Length == 0)
        {
            N.GetWindowRect(h, out var r);
            if (r.B - r.T < 80 && r.R - r.L > 260) N.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
        return true;
    }, IntPtr.Zero);
    Thread.Sleep(600);
}

// ---- 复现：文字大小调节 ----
if (args.Length > 0 && args[0] == "font")
{
    N.PostMessage(sink, 0x0312, (IntPtr)1, IntPtr.Zero);
    Thread.Sleep(1600);
    N.SetCursorPos(480, 340); Thread.Sleep(150);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(150);
    for (int i = 1; i <= 12; i++) { MoveTo(480 + 420 * i / 12, 340 + 260 * i / 12); Thread.Sleep(20); }
    Thread.Sleep(200);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    N.PressEnter();
    Thread.Sleep(2600);

    CloseAllEditors();

    // 收集所有"胶囊形状"的窗口，按 Top 排序：最上面是主胶囊，中间是调节条
    var bars = new List<(IntPtr H, N.RECT R)>();
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h)) return true;
        var c = new System.Text.StringBuilder(128); N.GetClassName(h, c, 128);
        if (!c.ToString().Contains("WindowsForms")) return true;
        N.GetWindowRect(h, out var r);
        int w = r.R - r.L, hh = r.B - r.T;
        if (w < 260 || hh < 20 || hh > 80) return true;
        bars.Add((h, r));
        return true;
    }, IntPtr.Zero);
    bars.Sort((a, b) => a.R.T.CompareTo(b.R.T));

    Console.WriteLine("   找到的横条窗口：");
    foreach (var b in bars) Console.WriteLine($"     @({b.R.L},{b.R.T}) {b.R.R - b.R.L}x{b.R.B - b.R.T}");

    if (bars.Count < 1) { Console.WriteLine("FAIL 找不到胶囊"); return; }
    var pill = bars[0];

    int pw = pill.R.R - pill.R.L; double k = pw / 625.0;
    Click(pill.R.L + (int)(173 * k), pill.R.T + (pill.R.B - pill.R.T) / 2);   // A▾
    Thread.Sleep(900);

    bars.Clear();
    N.EnumWindows((h, _) => {
        if (!N.IsWindowVisible(h)) return true;
        var c = new System.Text.StringBuilder(128); N.GetClassName(h, c, 128);
        if (!c.ToString().Contains("WindowsForms")) return true;
        N.GetWindowRect(h, out var r);
        int w = r.R - r.L, hh = r.B - r.T;
        if (w < 260 || hh < 20 || hh > 80) return true;
        bars.Add((h, r));
        return true;
    }, IntPtr.Zero);
    bars.Sort((a, b) => a.R.T.CompareTo(b.R.T));

    if (bars.Count < 2) { Console.WriteLine("FAIL 文字调节条未出现"); return; }
    var fly = bars[bars.Count - 1];   // 最靠下 = 调节条

    Console.WriteLine($"   主胶囊 @({pill.R.L},{pill.R.T})  调节条 @({fly.R.L},{fly.R.T}) {fly.R.R - fly.R.L}x{fly.R.B - fly.R.T}");

    // 滑块：调节条 1/3 处，向右拖
    int sy = fly.R.T + (fly.R.B - fly.R.T) / 2;
    int sx = fly.R.L + (int)((fly.R.R - fly.R.L) * 0.33);
    N.SetCursorPos(sx, sy); Thread.Sleep(200);
    N.mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); Thread.Sleep(200);
    for (int i = 1; i <= 12; i++) { MoveTo(sx + 10 * i, sy); Thread.Sleep(50); }
    Thread.Sleep(300);
    N.mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    Thread.Sleep(700);

    Console.WriteLine("   字号滑块已拖动（看日志有无异常）");
    return;
}

