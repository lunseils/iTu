using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using QqShot.Core;
using QqShot.Ui;

namespace QaCheck;

/// <summary>
/// 第 2 轮新增风险排查（针对 S1/S2 返工本身引入的风险）：
///   · 紧行距下"向外扫墨迹"会不会把相邻行的笔画一起盖掉（本改动最大风险）
///   · 行贴图片四边时的中位数取样（外侧不足 8px）
///   · 单行 / 超短行（1~2 字符）
///   · SamplePatch 为空时的回退路径
/// </summary>
internal static class Round2
{
    private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
    private static bool IsDark(Color c) => Luma(c) < 110;
    private static bool IsLight(Color c) => Luma(c) > 160;
    private static bool IsRed(Color c) => c.R > 150 && c.G < 120 && c.B < 120;
    private static int MaxCh(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private static int InkCount(Bitmap bmp, Rectangle r, Func<Color, bool> isInk)
    {
        r = Rectangle.Intersect(r, new Rectangle(0, 0, bmp.Width, bmp.Height));
        int n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                if (isInk(bmp.GetPixel(x, y))) n++;
        return n;
    }

    private static int RedCount(Bitmap bmp, Rectangle r)
    {
        r = Rectangle.Intersect(r, new Rectangle(0, 0, bmp.Width, bmp.Height));
        int n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                if (IsRed(bmp.GetPixel(x, y))) n++;
        return n;
    }

    private static Rectangle InkBoxIn(Bitmap bmp, Rectangle area, Func<Color, bool> isInk)
    {
        area = Rectangle.Intersect(area, new Rectangle(0, 0, bmp.Width, bmp.Height));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (isInk(bmp.GetPixel(x, y)))
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        return maxX < minX ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>补丁四边：内侧 1px 与外侧 1px 的单通道最大色差。</summary>
    private static (int Worst, int BadRows, int BadCols) EdgeDelta(Bitmap bmp, Rectangle patch)
    {
        int worst = 0, badRows = 0, badCols = 0;
        for (int y = patch.Top + 1; y < patch.Bottom - 1; y++)
        {
            int dL = 0, dR = 0;
            if (patch.Left - 1 >= 0) dL = MaxCh(bmp.GetPixel(patch.Left, y), bmp.GetPixel(patch.Left - 1, y));
            if (patch.Right < bmp.Width) dR = MaxCh(bmp.GetPixel(patch.Right - 1, y), bmp.GetPixel(patch.Right, y));
            int d = Math.Max(dL, dR);
            worst = Math.Max(worst, d);
            if (d > 3) badRows++;
        }
        for (int x = patch.Left + 1; x < patch.Right - 1; x++)
        {
            int dT = 0, dB = 0;
            if (patch.Top - 1 >= 0) dT = MaxCh(bmp.GetPixel(x, patch.Top), bmp.GetPixel(x, patch.Top - 1));
            if (patch.Bottom < bmp.Height) dB = MaxCh(bmp.GetPixel(x, patch.Bottom - 1), bmp.GetPixel(x, patch.Bottom));
            int d = Math.Max(dT, dB);
            worst = Math.Max(worst, d);
            if (d > 3) badCols++;
        }
        return (worst, badRows, badCols);
    }

    private static (TextBackdrop? Bd, Rectangle Box, int PadX, int PadY) Sample(Bitmap src, Rectangle box)
    {
        int padX = Math.Clamp((int)Math.Round(box.Height * 0.12), 2, 8);
        int padY = Math.Clamp((int)Math.Round(box.Height * 0.10), 1, 3);
        var bd = TextBackdrop.Sample(src, box, padX, padY);
        return (bd, box, padX, padY);
    }

    /// <summary>用 TextBackdrop 的结果构造标注并渲染。textColor 用于区分"新墨迹"。</summary>
    private static Bitmap Draw(Bitmap src, Rectangle box, string text, Color? textColor, out Rectangle patch, out TextBackdrop? bd)
    {
        var (b, _, padX, padY) = Sample(src, box);
        bd = b;
        patch = b?.Patch ?? Rectangle.Intersect(Rectangle.Inflate(box, padX, padY), new Rectangle(0, 0, src.Width, src.Height));
        var ann = new TranslationAnnotation
        {
            Kind = AnnotationKind.Text,
            Rect = box,
            Text = text,
            SamplePatch = b?.Patch ?? Rectangle.Empty,
            PadX = padX,
            PadY = padY,
            BackgroundColor = b?.MainBackground ?? Color.White,
            TextColor = textColor ?? b?.TextColor ?? Color.Black,
            RowLeft = b?.RowLeft,
            RowRight = b?.RowRight
        };
        var outBmp = new Bitmap(src);
        using var g = Graphics.FromImage(outBmp);
        ann.Draw(g);
        return outBmp;
    }

    /// <summary>诊断：产品自己的窗口枚举（_windowTable 的来源）到底看到了什么。</summary>
    public static void DumpSnapshotWindows(Report rep)
    {
        rep.Section("D0. 诊断：ScreenCapture.CaptureDesktop 的窗口表（吸附的数据源）");
        System.Diagnostics.Process? np = null;
        try
        {
            np = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            Thread.Sleep(2500);
            foreach (var w in W.VisibleWindows())
                if (w.Class.Contains("Notepad"))
                {
                    W.SetWindowPosPublic(w.Hwnd, new W.RECT { Left = 1180, Top = 640, Right = 1700, Bottom = 960 });
                    rep.Info($"已把记事本放到 {w.Rect}");
                }
            Thread.Sleep(800);

            var monitors = DisplayInfo.GetMonitors();
            var vb = DisplayInfo.GetVirtualBounds(monitors);
            var snap = QqShot.Core.ScreenCapture.CaptureDesktop(vb);
            rep.Info($"产品枚举到 {snap.Windows.Count} 个窗口：");
            foreach (var kv in snap.Windows)
                rep.Info($"   hwnd=0x{kv.Key.ToInt64():X} rect={kv.Value}");
            bool hasNotepad = snap.Windows.Values.Any(r => r.Width > 400 && r.Width < 700 && r.Height > 250 && r.Height < 400);
            rep.Check(hasNotepad, "产品窗口表包含锚点窗口（记事本）→ 吸附有目标可用",
                "产品窗口表不包含锚点窗口");

            // 关键诊断：ApplySnap 用"全局最小 EdgeDistance"挑唯一目标窗口。
            // 桌面级窗口（ApplicationFrameWindow / CoreWindow）横跨整屏，
            // 只要选区在它内部，EdgeDistance 恒为 0 → 它永远抢走"最优目标"，
            // 于是真正想对齐的窗口边缘吸附永远不生效。
            var probe = new Rectangle(1180, 633, 356, 300);   // 场景一：选区右下方覆盖记事本，左/上在外 7px
            static int EdgeDist(Rectangle a, Rectangle b)
            {
                int dx = a.Left < b.Left ? b.Left - a.Left : a.Left > b.Right ? a.Left - b.Right : 0;
                int dy = a.Top < b.Top ? b.Top - a.Top : a.Top > b.Bottom ? a.Top - b.Bottom : 0;
                return Math.Max(dx, dy);
            }
            rep.Info($"以选区 {probe} 为例，各候选窗口的距离：");
            Rectangle best = Rectangle.Empty; int bestD = int.MaxValue;
            foreach (var r in snap.Windows.Values)
            {
                int d = EdgeDist(probe, r);
                rep.Info($"   {r} → 距离 {d}{(d < bestD ? "  ← 目前最优" : "")}");
                if (d < bestD) { bestD = d; best = r; }
            }
            rep.Info($"最优目标 = {best}（距离 {bestD}）；它的四边与选区相差：左 {Math.Abs(probe.Left - best.Left)}、上 {Math.Abs(probe.Top - best.Top)}、右 {Math.Abs(probe.Right - best.Right)}、下 {Math.Abs(probe.Bottom - best.Bottom)}");
            bool hijacked = best.Width >= 1200 && best.Height >= 1000;   // 桌面级窗口
            rep.Check(bestD == 0 && hijacked,
                $"已复现抢目标机制：桌面级窗口 {best} 距离 0，抢走了最优目标，真正想对齐的窗口（距离 7）永远轮不到",
                $"未复现：最优目标 {best} 距离 {bestD}（笔记本应被选中）");
            snap.Image.Dispose();
        }
        catch (Exception ex) { rep.Fail_("诊断异常: " + ex.Message); }
        finally { try { np?.Kill(); } catch { } }
    }

    /// <summary>直测 ApplySnap 算法本身（把窗口表/选区用反射塞进去）。</summary>
    public static void SnapAlgorithm(Report rep)
    {
        rep.Section("D1. ApplySnap 算法直测（反射，隔离数据源问题）");
        try
        {
            var t = typeof(OverlaySession);
            var session = new OverlaySession(new AppSettings());
            var table = t.GetField("_windowTable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var sel = t.GetProperty("Selection", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var apply = t.GetMethod("ApplySnap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (table == null || sel == null || apply == null) { rep.Fail_("反射拿不到 _windowTable/Selection/ApplySnap"); return; }

            var target = new Rectangle(1187, 640, 506, 313);
            void Case(string name, Rectangle selection, int expectLeft, int expectTop)
            {
                table.SetValue(session, new List<Rectangle> { target });
                sel.SetValue(session, selection);
                apply.Invoke(session, null);
                var after = (Rectangle)sel.GetValue(session);
                rep.Check(after.Left == expectLeft && after.Top == expectTop,
                    $"{name}：选区 {selection} → {after}（期望左边 {expectLeft}、上边 {expectTop}）",
                    $"{name}：选区 {selection} → {after}（期望左边 {expectLeft}、上边 {expectTop}）");
            }

            Case("距 6px 吸附", new Rectangle(1181, 634, 356, 300), 1187, 640);
            Case("距 2px 吸附", new Rectangle(1185, 638, 356, 300), 1187, 640);
            Case("距 13px 不吸附", new Rectangle(1174, 627, 356, 300), 1174, 627);

            // 右/下边也要能在阈值内对齐
            table.SetValue(session, new List<Rectangle> { target });
            sel.SetValue(session, new Rectangle(1200, 650, 490, 306));   // right=1690（距 1693 差 3）、bottom=956（距 953 差 3）
            apply.Invoke(session, null);
            var a2 = (Rectangle)sel.GetValue(session);
            rep.Check(a2.Right == target.Right && a2.Bottom == target.Bottom,
                $"右边/下边也能吸附（{a2} → right={a2.Right} bottom={a2.Bottom}）",
                $"右边/下边没吸附（{a2}，期望 right={target.Right} bottom={target.Bottom}）");
            session.Dispose();
        }
        catch (Exception ex) { rep.Fail_("ApplySnap 直测异常: " + ex.Message); }
    }

    // ---------------- 风险 1：紧行距（最担心的一处） ----------------

    public static void TightSpacing(Report rep)
    {
        rep.Section("R1. 紧行距：向外扫墨迹会不会吃掉相邻行（本改动最大风险）");
        try
        {
            // 三行，行间精确留 2px；上一行带降部 g/y/p/j，下一行带升部 T/H/d
            string[] texts = { "Quick jumpy", "grumpy dogs", "TH HHH 直立" };
            using var probeFont = new Font("Microsoft YaHei UI", 26, FontStyle.Regular, GraphicsUnit.Pixel);

            // 第一步：单独量出每行文字相对绘制原点的墨迹上下偏移，才能精确控制行距
            var offs = new List<(int Top, int Bottom)>();
            foreach (var t in texts)
            {
                using var tmp = new Bitmap(620, 80);
                using (var g = Graphics.FromImage(tmp))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.DrawString(t, probeFont, Brushes.Black, 12, 10);
                }
                var ib = InkBoxIn(tmp, new Rectangle(0, 0, tmp.Width, tmp.Height), IsDark);
                offs.Add((ib.Top - 10, ib.Bottom - 10));
            }
            rep.Info("各行墨迹偏移(相对原点): " + string.Join(" ", offs.Select(o => $"[{o.Top},{o.Bottom})")));

            int y1 = 10;
            int b1Top = y1 + offs[0].Top, b1Bot = y1 + offs[0].Bottom;
            int y2 = b1Bot + 1 - offs[1].Top;                 // 与上一行留 2px
            int b2Top = y2 + offs[1].Top, b2Bot = y2 + offs[1].Bottom;
            int y3 = b2Bot + 1 - offs[2].Top;                 // 与下一行留 2px
            rep.Info($"绘制原点 y = {y1}/{y2}/{y3}");

            var srcBmp = new Bitmap(620, 170);
            using (var g = Graphics.FromImage(srcBmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(texts[0], probeFont, Brushes.Black, 12, y1);
                g.DrawString(texts[1], probeFont, Brushes.Black, 12, y2);
                g.DrawString(texts[2], probeFont, Brushes.Black, 12, y3);
            }
            var src = srcBmp;

            // 逐行统计墨迹，复算实际墨迹带
            var inkRows = new bool[src.Height];
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                    if (IsDark(src.GetPixel(x, y))) { inkRows[y] = true; break; }
            var bands = new List<(int Top, int Bottom)>();
            int st = -1;
            for (int y = 0; y < src.Height; y++)
            {
                if (inkRows[y] && st < 0) st = y;
                if (!inkRows[y] && st >= 0) { bands.Add((st, y)); st = -1; }
            }
            if (st >= 0) bands.Add((st, src.Height));
            rep.Info("墨迹带: " + string.Join(" ", bands.Select(b => $"[{b.Top},{b.Bottom})")));
            if (bands.Count < 3) { rep.Fail_($"只得到 {bands.Count} 段墨迹带，无法验证紧行距"); return; }

            int gap1 = bands[1].Top - bands[0].Bottom;
            int gap2 = bands[2].Top - bands[1].Bottom;
            rep.Info($"行间空隙: {gap1}px / {gap2}px（越小越危险）");
            rep.Check(gap1 <= 3 && gap2 <= 3, $"行距已足够紧（{gap1}/{gap2}px ≤3）", $"行距不够紧（{gap1}/{gap2}），命题无效");

            // 中间行：模拟 OCR 只给"主体"框（上下各切掉一部分，降部/升部在框外）
            var band = bands[1];
            var midInk = InkBoxIn(src, new Rectangle(0, band.Top - 1, src.Width, band.Bottom - band.Top + 2), IsDark);
            var box = Rectangle.FromLTRB(midInk.Left, midInk.Top + 3, midInk.Right, midInk.Bottom - 3);
            rep.Info($"中间行墨迹 {midInk}，模拟 OCR 行框 {box}");

            var (bd, _, _, _) = Sample(src, box);
            rep.Check(bd != null, "中间行采样成功", "中间行采样失败");
            if (bd == null) return;

            rep.Info($"补丁 {bd.Patch}；上一行墨迹底 {bands[0].Bottom}，下一行墨迹顶 {bands[2].Top}");
            rep.Check(bd.Patch.Top >= bands[0].Bottom,
                $"补丁顶 {bd.Patch.Top} 未侵入上一行（上一行最后墨迹行 {bands[0].Bottom - 1}）",
                $"补丁顶 {bd.Patch.Top} 已侵入上一行（上一行最后墨迹行 {bands[0].Bottom - 1}）→ 会盖掉上一行文字");
            rep.Check(bd.Patch.Bottom <= bands[2].Top,
                $"补丁底 {bd.Patch.Bottom} 未侵入下一行（下一行墨迹顶 {bands[2].Top}）",
                $"补丁底 {bd.Patch.Bottom} 已侵入下一行（下一行墨迹顶 {bands[2].Top}）→ 会盖掉下一行文字");

            // 真渲染后，相邻两行的墨迹必须一个不少
            using var final = Draw(src, box, "替换", Color.Red, out _, out _);
            int prevBefore = InkCount(src, new Rectangle(0, bands[0].Top, src.Width, bands[0].Bottom - bands[0].Top), IsDark);
            int prevAfter = InkCount(final, new Rectangle(0, bands[0].Top, src.Width, bands[0].Bottom - bands[0].Top), IsDark);
            int nextBefore = InkCount(src, new Rectangle(0, bands[2].Top, src.Width, bands[2].Bottom - bands[2].Top), IsDark);
            int nextAfter = InkCount(final, new Rectangle(0, bands[2].Top, src.Width, bands[2].Bottom - bands[2].Top), IsDark);
            rep.Check(prevAfter >= prevBefore * 0.98 && nextAfter >= nextBefore * 0.98,
                $"相邻两行文字完好（上一行 {prevBefore}→{prevAfter}，下一行 {nextBefore}→{nextAfter}）",
                $"相邻行被吃掉：上一行 {prevBefore}→{prevAfter}，下一行 {nextBefore}→{nextAfter}");

            // 上下相邻行本身就是墨迹，"补丁边界内外色差"在上下方向无意义，只查左右两侧
            int worstLr = 0;
            for (int y = bd.Patch.Top; y < bd.Patch.Bottom; y++)
            {
                if (bd.Patch.Left - 1 >= 0)
                    worstLr = Math.Max(worstLr, MaxCh(final.GetPixel(bd.Patch.Left, y), final.GetPixel(bd.Patch.Left - 1, y)));
                if (bd.Patch.Right < final.Width)
                    worstLr = Math.Max(worstLr, MaxCh(final.GetPixel(bd.Patch.Right - 1, y), final.GetPixel(bd.Patch.Right, y)));
            }
            rep.Check(worstLr <= 3, $"紧行距下补丁左右边界无色差（Δmax={worstLr}）",
                $"紧行距下补丁左右边界有明显色差 Δmax={worstLr}");

            var dir = Path.Combine(Path.GetTempPath(), "qqshot_qa");
            Directory.CreateDirectory(dir);
            final.Save(Path.Combine(dir, "r2_tight_spacing.png"), ImageFormat.Png);
            src.Save(Path.Combine(dir, "r2_tight_spacing_src.png"), ImageFormat.Png);
        }
        catch (Exception ex) { rep.Fail_("紧行距测试异常: " + ex); }
    }

    // ---------------- 风险 2：行贴图片四边 ----------------

    public static void ImageEdges(Report rep)
    {
        rep.Section("R2. 行贴图片四边（外侧不足 8px）时取样是否安全");
        try
        {
            var cases = new (string Name, int W, int H, Point At, string Text, Func<Color, bool> Ink)[]
            {
                ("贴顶", 320, 90, new Point(20, -6), "Edge Top 顶部", IsDark),
                ("贴左", 320, 90, new Point(-4, 26), "Edge Left 左侧", IsDark),
                ("贴右", 320, 90, new Point(140, 26), "Right边缘贴合测试", IsDark),
                ("贴底", 320, 90, new Point(20, 52), "Bottom 底部 gyp", IsDark),
                ("深底贴边", 320, 90, new Point(-2, -4), "Dark Edge 深底", IsLight),
            };

            foreach (var c in cases)
            {
                using var src = new Bitmap(c.W, c.H);
                using (var g = Graphics.FromImage(src))
                {
                    g.Clear(c.Name.StartsWith("深底") ? Color.FromArgb(28, 30, 36)
                                                      : Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using var f = new Font("Microsoft YaHei UI", 26, FontStyle.Regular, GraphicsUnit.Pixel);
                    g.DrawString(c.Text, f, c.Name.StartsWith("深底") ? Brushes.White : Brushes.Black, c.At.X, c.At.Y);
                }

                var ink = InkBoxIn(src, new Rectangle(0, 0, c.W, c.H), c.Ink);
                if (ink.IsEmpty) { rep.Fail_($"[{c.Name}] 没造出文字"); continue; }
                // OCR 行框 = 墨迹框（贴边时可能正好压到 0 或 W-1）
                var box = ink;

                TextBackdrop? bd = null;
                string err = "";
                try { (bd, _, _, _) = Sample(src, box); }
                catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }

                bool inside = bd != null &&
                    bd.Patch.Left >= 0 && bd.Patch.Top >= 0 &&
                    bd.Patch.Right <= c.W && bd.Patch.Bottom <= c.H;

                rep.Check(err == "" && bd != null && inside,
                    $"[{c.Name}] 墨迹 {ink} → 补丁 {bd?.Patch} 安全（无异常、不越界）",
                    $"[{c.Name}] 墨迹 {ink} 取样异常/越界：err={err} 补丁={bd?.Patch}");

                if (bd == null) continue;

                using var final = Draw(src, box, "替换 OK", Color.Red, out var patch, out _);
                // 注意：纯红译文 luma≈76，会被 IsDark 判成墨迹 → 残留统计必须排除红色
                int resid = InkCount(final, Rectangle.Inflate(box, 4, 6), x => c.Ink(x) && !IsRed(x));
                rep.Check(resid <= 2, $"[{c.Name}] 贴边行原文已清干净（残留 {resid}）",
                    $"[{c.Name}] 贴边行原文残留 {resid} 像素");

                int red = RedCount(final, patch);
                rep.Check(red > 20, $"[{c.Name}] 贴边行译文已绘制（红字 {red} 像素）",
                    $"[{c.Name}] 贴边行译文没画出来（红字 {red}）");
            }
        }
        catch (Exception ex) { rep.Fail_("贴边测试异常: " + ex); }
    }

    // ---------------- 风险 3：单行 / 超短行 ----------------

    public static void ShortLines(Report rep)
    {
        rep.Section("R3. 单行 / 超短行（1~2 字符）");
        try
        {
            var cases = new (string Name, string Text, int W, int H, int FontPx)[]
            {
                ("2字符", "OK", 160, 56, 26),
                ("1汉字", "中", 120, 56, 26),
                ("1标点", "，", 120, 56, 26),
                ("超小字号", "Ab", 120, 30, 11),
            };

            foreach (var c in cases)
            {
                using var src = new Bitmap(c.W, c.H);
                using (var g = Graphics.FromImage(src))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using var f = new Font("Microsoft YaHei UI", c.FontPx, FontStyle.Regular, GraphicsUnit.Pixel);
                    g.DrawString(c.Text, f, Brushes.Black, 6, 6);
                }

                var ink = InkBoxIn(src, new Rectangle(0, 0, c.W, c.H), IsDark);
                if (ink.IsEmpty) { rep.Fail_($"[{c.Name}] 没造出文字" ); continue; }

                TextBackdrop? bd = null;
                string err = "";
                try { (bd, _, _, _) = Sample(src, ink); }
                catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }

                rep.Check(err == "" && bd != null,
                    $"[{c.Name}] 取样成功（墨迹 {ink} → 补丁 {bd?.Patch}）",
                    $"[{c.Name}] 取样失败/异常：err={err}");
                if (bd == null) continue;

                using var final = Draw(src, ink, "替换", Color.Red, out var patch, out _);
                int resid = InkCount(final, Rectangle.Inflate(ink, 6, 6), x => IsDark(x) && !IsRed(x));
                int red = RedCount(final, patch);
                rep.Check(resid <= 2, $"[{c.Name}] 原文已清干净（残留 {resid}）", $"[{c.Name}] 原文残留 {resid}");
                rep.Check(red > 8, $"[{c.Name}] 译文已绘制（红字 {red}）", $"[{c.Name}] 译文没画出来（红字 {red}）");
                var d = EdgeDelta(final, bd.Patch);
                rep.Check(d.Worst <= 3, $"[{c.Name}] 补丁边界无色差（Δmax={d.Worst}）", $"[{c.Name}] 补丁边界色差 {d.Worst}");
            }
        }
        catch (Exception ex) { rep.Fail_("短行测试异常: " + ex); }
    }

    // ---------------- 风险 4：SamplePatch 为空的回退路径 ----------------

    public static void NullPatchFallback(Report rep)
    {
        rep.Section("R4. SamplePatch 为空时的回退路径（旧的固定 padding 逻辑）");
        try
        {
            using var src = new Bitmap(300, 80);
            using (var g = Graphics.FromImage(src))
            {
                g.Clear(Color.FromArgb(245, 246, 250));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var f = new Font("Microsoft YaHei UI", 24, FontStyle.Regular, GraphicsUnit.Pixel);
                g.DrawString("Fallback 回退", f, Brushes.Black, 10, 22);
            }

            var ann = new TranslationAnnotation
            {
                Kind = AnnotationKind.Text,
                Rect = new Rectangle(10, 22, 220, 30),
                Text = "兜底译文",
                SamplePatch = Rectangle.Empty,     // ← 故意不给采样区域
                RowLeft = null,
                RowRight = null,
                PadX = 3,
                PadY = 2,
                BackgroundColor = Color.FromArgb(245, 246, 250),
                TextColor = Color.Red
            };

            using var final = new Bitmap(src);
            string err = "";
            try
            {
                using var g = Graphics.FromImage(final);
                ann.Draw(g);
            }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }

            rep.Check(err == "", "SamplePatch 为空时不抛异常", "SamplePatch 为空时抛异常：" + err);

            var expect = Rectangle.Inflate(new Rectangle(10, 22, 220, 30), 3, 2);
            int red = RedCount(final, Rectangle.Inflate(expect, 2, 2));
            rep.Check(red > 20, $"回退路径仍画出译文（红字 {red} 像素，补丁≈{expect}）",
                $"回退路径没画出译文（红字 {red}）");

            // 译文必须落在 Rect±(PadX,PadY) 附近，不能跑到别处
            var redBox = InkBoxIn(final, new Rectangle(0, 0, final.Width, final.Height), IsRed);
            rep.Check(!redBox.IsEmpty && redBox.Top >= expect.Top - 1 && redBox.Bottom <= expect.Bottom + 1,
                $"回退译文位置正确（{redBox} ⊂ {expect}）",
                $"回退译文位置异常（{redBox} vs {expect}）");
        }
        catch (Exception ex) { rep.Fail_("回退路径测试异常: " + ex); }
    }
}
