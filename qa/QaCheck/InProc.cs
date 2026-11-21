using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using QqShot.Core;
using QqShot.Ui;

namespace QaCheck;

/// <summary>
/// 不依赖 GUI 的独立验证：抓屏位块质量、放大镜瓦片缓存、译文原位替换。
/// </summary>
internal static class InProc
{
    // ---------------- E1. BitBlt 抓屏 ----------------

    public static void CaptureTests(Report rep)
    {
        rep.Section("E1. BitBlt 抓屏质量（对齐 CopyFromScreen / alpha / 非纯黑）");
        try
        {
            var monitors = DisplayInfo.GetMonitors();
            var vb = DisplayInfo.GetVirtualBounds(monitors);
            var snap = ScreenCapture.CaptureDesktop(vb);
            var a = snap.Image;

            using var b = new Bitmap(vb.Width, vb.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
                g.CopyFromScreen(vb.Left, vb.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);

            int total = 0, exact = 0, maxSum = 0;
            for (int y = 5; y < b.Height - 5; y += 53)
                for (int x = 5; x < b.Width - 5; x += 53)
                {
                    var p1 = a.GetPixel(x, y);
                    var p2 = b.GetPixel(x, y);
                    total++;
                    int d = Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B);
                    if (d == 0) exact++;
                    if (d > maxSum) maxSum = d;
                }
            rep.Info($"逐点比对 {total} 点：完全一致 {exact}，最大通道差之和 {maxSum}");
            rep.Check(exact >= total * 0.98,
                $"BitBlt 与 CopyFromScreen 像素一致（{exact}/{total}）",
                $"BitBlt 与 CopyFromScreen 不一致（仅 {exact}/{total} 一致，最大差 {maxSum}）");

            // alpha 全 255
            var data = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int notOpaque = 0, blackish = 0;
            long lumaSum = 0;
            try
            {
                var row = new int[a.Width];
                for (int y = 0; y < a.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < row.Length; x++)
                    {
                        int p = row[x];
                        if (((p >> 24) & 0xFF) != 255) notOpaque++;
                        int r = (p >> 16) & 0xFF, g2 = (p >> 8) & 0xFF, b2 = p & 0xFF;
                        int lum = (r * 299 + g2 * 587 + b2 * 114) / 1000;
                        lumaSum += lum;
                        if (lum < 8) blackish++;
                    }
                }
            }
            finally { a.UnlockBits(data); }

            long pixels = (long)a.Width * a.Height;
            rep.Info($"alpha≠255 的像素 {notOpaque} / {pixels}；暗像素(luma<8) {blackish}；平均亮度 {lumaSum / pixels:F1}");
            rep.Check(notOpaque == 0, "ForceOpaque 生效：全图 alpha=255", $"仍有 {notOpaque} 个像素 alpha≠255（会被当透明图）");
            rep.Check(lumaSum / pixels > 8, "画面非纯黑（确实抓到内容）",
                $"平均亮度仅 {lumaSum / pixels:F1}，疑似抓屏失败");
            rep.Check(snap.Windows.Count > 0, $"窗口枚举正常（{snap.Windows.Count} 个）", "窗口枚举为空");

            // 右下角（BitBlt 越界/跨 DC 易错点）
            var corner = a.GetPixel(a.Width - 2, a.Height - 2);
            rep.Info($"右下角像素 {corner}");
            rep.Check(corner.A == 255, "右下角像素有效", "右下角像素 alpha 异常");
            a.Dispose();
        }
        catch (Exception ex)
        {
            rep.Fail_("BitBlt 抓屏测试异常: " + ex);
        }
    }

    // ---------------- E2. 放大镜瓦片缓存 ----------------

    public static void SamplerTests(Report rep)
    {
        rep.Section("E2. 放大镜 96×96 瓦片缓存刷新（反射直测 SnapshotSampler）");
        try
        {
            const int W = 2560, H = 1440;
            using var src = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            // 坐标编码色：R 只随 x 变、G 只随 y 变、B 随两者变 → 任何陈旧偏移都会立刻暴露
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    src.SetPixel(x, y, Color.FromArgb(255, (x * 7) & 0xFF, (y * 11) & 0xFF, (x * 31 + y * 17) & 0xFF));

            var nested = typeof(OverlaySession).GetNestedType("SnapshotSampler", BindingFlags.NonPublic);
            if (nested == null) { rep.Fail_("找不到内部类型 SnapshotSampler（实现已变更，测试需更新）"); return; }

            var ctor = nested.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Bitmap) }, null);
            var getColor = nested.GetMethod("GetColor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var fill = nested.GetMethod("FillMagnifier", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (ctor == null || getColor == null || fill == null) { rep.Fail_("SnapshotSampler 反射签名不匹配"); return; }

            object sampler = ctor.Invoke(new object[] { src });

            // 1) 远距离跳点序列（含来回、含四角）
            var jumps = new (int X, int Y)[]
            {
                (300, 200), (1500, 800), (2300, 300), (300, 200),   // 回到原位
                (2559, 1439), (0, 0), (1280, 720), (2300, 300), (2559, 1439), (301, 201)
            };
            int bad = 0;
            var detail = new List<string>();
            foreach (var p in jumps)
            {
                var got = (Color)getColor.Invoke(sampler, new object[] { p.X, p.Y })!;
                var want = src.GetPixel(p.X, p.Y);
                if (got.ToArgb() != want.ToArgb())
                {
                    bad++;
                    detail.Add($"({p.X},{p.Y}) 取到 {got} 实际 {want}");
                }
            }
            rep.Check(bad == 0, $"跳点取色全部正确（{jumps.Length} 点，含远距离来回）",
                $"跳点取色有 {bad} 个陈旧/错误：{string.Join("；", detail)}");

            // 2) 全图密集抽样（捕捉边界 clamp 后的错位）
            int n2 = 0, bad2 = 0;
            for (int y = 0; y < H; y += 37)
                for (int x = 0; x < W; x += 37)
                {
                    n2++;
                    var got = (Color)getColor.Invoke(sampler, new object[] { x, y })!;
                    if (got.ToArgb() != src.GetPixel(x, y).ToArgb()) bad2++;
                }
            rep.Check(bad2 == 0, $"密集抽样 {n2} 点全部正确", $"密集抽样 {n2} 点中有 {bad2} 点错色（瓦片未重锁）");

            // 3) 放大镜 11×11 取样图（FillMagnifier）：验证中心像素与整格内容
            int bad3 = 0, badCell = 0;
            var centers = new[] { (300, 200), (1500, 800), (2559, 1439), (0, 0), (2300, 300) };
            foreach (var c in centers)
            {
                using var lens = new Bitmap(11, 11, PixelFormat.Format32bppArgb);
                fill.Invoke(sampler, new object[] { lens, c.Item1, c.Item2, 11 });
                for (int dy = -5; dy <= 5; dy++)
                    for (int dx = -5; dx <= 5; dx++)
                    {
                        int sx = c.Item1 + dx, sy = c.Item2 + dy;
                        Color want = (sx < 0 || sy < 0 || sx >= W || sy >= H)
                            ? Color.FromArgb(0, 0, 0, 0)
                            : src.GetPixel(sx, sy);
                        if (lens.GetPixel(dx + 5, dy + 5).ToArgb() != want.ToArgb()) bad3++;
                    }
                if (lens.GetPixel(5, 5).ToArgb() != src.GetPixel(c.Item1, c.Item2).ToArgb()) badCell++;
            }
            rep.Check(bad3 == 0, $"放大镜网格 11×11×{centers.Length} 像素全部与源图一致",
                $"放大镜网格有 {bad3} 个像素错色（中心格错 {badCell}）");
        }
        catch (Exception ex)
        {
            rep.Fail_("放大镜瓦片测试异常: " + ex);
        }
    }

    // ---------------- D. 译文原位替换 ----------------

    private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    private static int InkCount(Bitmap bmp, Rectangle r, Func<Color, bool> isInk)
    {
        r = Rectangle.Intersect(r, new Rectangle(0, 0, bmp.Width, bmp.Height));
        int n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                if (isInk(bmp.GetPixel(x, y))) n++;
        return n;
    }

    private static Rectangle InkBoxIn(Bitmap bmp, Rectangle area, Func<Color, bool> isInk)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (isInk(bmp.GetPixel(x, y)))
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < minX) return Rectangle.Empty;
        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static int MaxChannelDiff(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>构造并渲染一条译文标注（新 API：补丁范围由 TextBackdrop.Patch 决定）。</summary>
    private static Bitmap Render(Bitmap src, Rectangle box, string text, out Rectangle patch,
        out Color bgOut, out Color inkOut)
    {
        int padX = Math.Clamp((int)Math.Round(box.Height * 0.12), 2, 8);
        int padY = Math.Clamp((int)Math.Round(box.Height * 0.10), 1, 3);
        var bd = TextBackdrop.Sample(src, box, padX, padY);
        patch = bd?.Patch ?? Rectangle.Intersect(Rectangle.Inflate(box, padX, padY),
            new Rectangle(0, 0, src.Width, src.Height));

        var ann = new TranslationAnnotation
        {
            Kind = AnnotationKind.Text,
            Rect = box,
            Text = text,
            SamplePatch = bd?.Patch ?? Rectangle.Empty,
            PadX = padX,
            PadY = padY,
            BackgroundColor = bd?.MainBackground ?? Color.White,
            TextColor = bd?.TextColor ?? Color.Black,
            RowLeft = bd?.RowLeft,
            RowRight = bd?.RowRight
        };
        bgOut = bd?.MainBackground ?? Color.White;
        inkOut = bd?.TextColor ?? Color.Black;

        var outBmp = new Bitmap(src);
        using var g = Graphics.FromImage(outBmp);
        ann.Draw(g);
        return outBmp;
    }

    public static void TranslateTests(Report rep)
    {
        rep.Section("D. 译文原位替换（纯白/深底/横向渐变 × 中英文行）");
        try
        {
            var settings = new AppSettings { TranslateTarget = "zh-CN" };

            var cases = new (string Name, Func<Bitmap> Build, Func<Color, bool> IsInk, int ShrinkRight)[]
            {
                ("white", () =>
                {
                    var b = new Bitmap(560, 130);
                    using var g = Graphics.FromImage(b);
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using var f = new Font("Microsoft YaHei UI", 26, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var f2 = new Font("Microsoft YaHei UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
                    g.DrawString("Hello World 你好", f2, Brushes.Black, 14, 24);
                    g.DrawString("Settings 设置项", f, Brushes.Black, 14, 72);
                    return b;
                }, c => Luma(c) < 110, 0),

                // S1 复现用例：把行框右边界推进字形内部 2px，让边缘列落在抗锯齿上
                ("white-tight", () =>
                {
                    var b = new Bitmap(560, 130);
                    using var g = Graphics.FromImage(b);
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using var f = new Font("Microsoft YaHei UI", 26, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var f2 = new Font("Microsoft YaHei UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
                    g.DrawString("Hello World 你好", f2, Brushes.Black, 14, 24);
                    g.DrawString("Settings 设置项", f, Brushes.Black, 14, 72);
                    return b;
                }, c => Luma(c) < 110, 2),

                ("dark", () =>
                {
                    var b = new Bitmap(560, 130);
                    using var g = Graphics.FromImage(b);
                    g.Clear(Color.FromArgb(28, 30, 36));
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using var f = new Font("Microsoft YaHei UI", 26, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var f2 = new Font("Microsoft YaHei UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
                    g.DrawString("Hello World 你好", f2, Brushes.White, 14, 24);
                    g.DrawString("Settings 设置项", f, Brushes.White, 14, 72);
                    return b;
                }, c => Luma(c) > 160, 0),

                ("gradient", () =>
                {
                    var b = new Bitmap(560, 130);
                    using var g = Graphics.FromImage(b);
                    using (var lg = new LinearGradientBrush(new Rectangle(0, 0, 560, 130),
                               Color.FromArgb(238, 246, 255), Color.FromArgb(112, 162, 236), 8f))
                        g.FillRectangle(lg, 0, 0, 560, 130);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using var f = new Font("Microsoft YaHei UI", 26, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var f2 = new Font("Microsoft YaHei UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
                    g.DrawString("Hello World 你好", f2, Brushes.Black, 14, 24);
                    g.DrawString("Settings 设置项", f, Brushes.Black, 14, 72);
                    return b;
                }, c => Luma(c) < 110, 0),
            };

            foreach (var cs in cases)
            {
                using var src = cs.Build();
                var lines = OcrService.RecognizeLinesAsync(src).GetAwaiter().GetResult();
                rep.Info($"[{cs.Name}] OCR {lines.Count} 行: " +
                         string.Join(" | ", lines.Select(l => $"\"{l.Text}\"@{l.Bounds}")));
                if (lines.Count == 0) { rep.Fail_($"[{cs.Name}] OCR 未识别到任何行"); continue; }

                var texts = lines.Select(l => l.Text).ToList();
                var translated = TranslateService.TranslateLinesAsync(texts, settings).GetAwaiter().GetResult();
                bool same = texts.Select((t, i) => string.Equals(t.Trim(), translated[i]?.Trim(), StringComparison.OrdinalIgnoreCase)).All(x => x);
                rep.Info($"[{cs.Name}] 译文: {string.Join(" | ", translated)}" + (same ? "  （与原文相同 → 疑似离线回退）" : ""));
                if (same) rep.Warn($"[{cs.Name}] 译文字符串与原文相同：网络翻译未生效，下面改用固定译文验证渲染");

                foreach (var line in lines)
                {
                    var idx = lines.ToList().IndexOf(line);
                    string shown = same ? "确定 OK" : (translated[idx]?.Trim() is { Length: > 0 } s2 ? s2 : "确定 OK");
                    // 模拟 OCR 行框贴紧字形（white-tight 用例把右边界推进 2px）
                    var box = cs.ShrinkRight > 0
                        ? Rectangle.FromLTRB(line.Bounds.Left, line.Bounds.Top,
                            Math.Max(line.Bounds.Left + 4, line.Bounds.Right - cs.ShrinkRight), line.Bounds.Bottom)
                        : line.Bounds;
                    rep.Info($"[{cs.Name}] 行 {idx} 行框 {box}（OCR {line.Bounds}）");

                    // (a) 只还原背景、不画译文 → 原文墨迹必须彻底消失
                    using (var erased = Render(src, box, " ", out var patch, out _, out _))
                    {
                        // 这一行的真实墨迹范围（含降部），用于判断补丁是否盖全
                        var lineInk = InkBoxIn(src, Rectangle.Intersect(
                            Rectangle.Inflate(box, 0, 12), new Rectangle(0, 0, src.Width, src.Height)), cs.IsInk);
                        rep.Info($"[{cs.Name}] 行 {idx} 实际墨迹 {lineInk} / 补丁 {patch}（补丁底 {patch.Bottom} vs 墨迹底 {lineInk.Bottom}）");
                        rep.Check(!lineInk.IsEmpty && patch.Top <= lineInk.Top && patch.Bottom >= lineInk.Bottom,
                            $"[{cs.Name}] 行 {idx} 补丁盖全墨迹（补丁 {patch.Top}..{patch.Bottom} vs 墨迹 {lineInk.Top}..{lineInk.Bottom}）",
                            $"[{cs.Name}] 行 {idx} 补丁没盖全墨迹（补丁 {patch.Top}..{patch.Bottom} vs 墨迹 {lineInk.Top}..{lineInk.Bottom}）");

                        int ink = InkCount(erased, box, cs.IsInk);
                        int inkDil = InkCount(erased, Rectangle.Inflate(box, 1, 1), cs.IsInk);
                        rep.Check(ink <= 2,
                            $"[{cs.Name}] 行 {idx} 行框内原文墨迹已清除（{ink} 残留）",
                            $"[{cs.Name}] 行 {idx} 行框内仍残留原文墨迹 {ink} 像素");
                        rep.Check(inkDil <= 8,
                            $"[{cs.Name}] 行 {idx} 行框外扩 1px 也无泄漏（{inkDil} 残留）",
                            $"[{cs.Name}] 行 {idx} 行框外扩 1px 仍残留 {inkDil} 像素");

                        var wide = Rectangle.Inflate(box, 8, 8);
                        int inkWide = InkCount(erased, wide, cs.IsInk);
                        var wideBox = InkBoxIn(erased, wide, cs.IsInk);
                        rep.Check(inkWide <= 2,
                            $"[{cs.Name}] 行 {idx} 行框±8px 内无原文残留（{inkWide} 像素）",
                            $"[{cs.Name}] 行 {idx} 行框±8px 内残留原文 {inkWide} 像素 @{wideBox}");

                        // (b) 补丁四边逐行/逐列扫描色差（单通道最大差）
                        int worst = 0, badRowsN = 0, badColsN = 0;
                        var badRows = new List<int>();
                        for (int y = patch.Top + 1; y < patch.Bottom - 1; y++)
                        {
                            int dL = MaxChannelDiff(erased.GetPixel(patch.Left + 1, y),
                                                    erased.GetPixel(Math.Max(0, patch.Left - 2), y));
                            int dR = MaxChannelDiff(erased.GetPixel(patch.Right - 2, y),
                                                    erased.GetPixel(Math.Min(erased.Width - 1, patch.Right + 1), y));
                            int d = Math.Max(dL, dR);
                            if (d > worst) worst = d;
                            if (d > 3) { badRowsN++; badRows.Add(y); }
                        }
                        for (int x = patch.Left + 1; x < patch.Right - 1; x++)
                        {
                            int dT = MaxChannelDiff(erased.GetPixel(x, patch.Top + 1),
                                                    erased.GetPixel(x, Math.Max(0, patch.Top - 2)));
                            int dB = MaxChannelDiff(erased.GetPixel(x, patch.Bottom - 2),
                                                    erased.GetPixel(x, Math.Min(erased.Height - 1, patch.Bottom + 1)));
                            int d = Math.Max(dT, dB);
                            if (d > worst) worst = d;
                            if (d > 3) badColsN++;
                        }
                        string rowsDesc = badRows.Count == 0 ? "无" : string.Join(",", badRows.Take(6));
                        rep.Info($"[{cs.Name}] 行 {idx} 补丁边界 Δmax={worst}，超阈值行 {badRowsN} [{rowsDesc}]，列 {badColsN}");
                        rep.Check(worst <= 3, $"[{cs.Name}] 行 {idx} 补丁边界无色差（Δmax={worst}）",
                            $"[{cs.Name}] 行 {idx} 补丁边界有明显色差 Δmax={worst}（{badRowsN} 行 / {badColsN} 列 >3）");
                    }

                    // (d) 真实渲染
                    using (var final = Render(src, box, shown, out var patch, out var bg, out var ink))
                    {
                        var shownInk = InkCount(final, patch, cs.IsInk);
                        rep.Check(shownInk > 20,
                            $"[{cs.Name}] 行 {idx} 译文已绘制（补丁内墨迹 {shownInk} 像素）",
                            $"[{cs.Name}] 行 {idx} 译文没有出现（{shownInk}）");

                        double contrast = Math.Abs(Luma(ink) - Luma(bg));
                        rep.Check(contrast >= 60,
                            $"[{cs.Name}] 行 {idx} 译文对比度足够（Δluma={contrast:F0}）",
                            $"[{cs.Name}] 行 {idx} 译文对比度不足（Δluma={contrast:F0}）");

                        var scanBox = Rectangle.Intersect(Rectangle.Inflate(patch, 2, 2), new Rectangle(0, 0, final.Width, final.Height));
                        var textBox = InkBoxIn(final, scanBox, cs.IsInk);
                        rep.Check(!textBox.IsEmpty && textBox.Left >= patch.Left - 1 && textBox.Right <= patch.Right + 1 &&
                                  textBox.Top >= patch.Top - 1 && textBox.Bottom <= patch.Bottom + 1,
                            $"[{cs.Name}] 行 {idx} 译文未溢出补丁",
                            $"[{cs.Name}] 行 {idx} 译文溢出/缺失（{textBox} vs 补丁 {patch}）");

                        var dir = Path.Combine(Path.GetTempPath(), "qqshot_qa");
                        Directory.CreateDirectory(dir);
                        final.Save(Path.Combine(dir, $"tr_{cs.Name}_{idx}.png"), ImageFormat.Png);
                        src.Save(Path.Combine(dir, $"tr_{cs.Name}_{idx}_src.png"), ImageFormat.Png);
                    }
                }
                var outDir = Path.Combine(Path.GetTempPath(), "qqshot_qa");
                rep.Info($"[{cs.Name}] 输出图目录 {outDir}");
            }
        }
        catch (Exception ex)
        {
            rep.Fail_("译文原位替换测试异常: " + ex);
        }
    }
}
