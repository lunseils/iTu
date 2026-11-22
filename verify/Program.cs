using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using iTu.Core;
using iTu.Ui;

var log = new System.Text.StringBuilder();
void Section(string s) => log.AppendLine($"\n=== {s} ===");
void Ok(string s) => log.AppendLine($"  [OK]   {s}");
void Fail(string s) => log.AppendLine($"  [FAIL] {s}");
void Check(bool cond, string s) { if (cond) Ok(s); else Fail(s); }

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

Section("1. 显示器信息");
try
{
    var monitors = DisplayInfo.GetMonitors();
    var vb = DisplayInfo.GetVirtualBounds(monitors);
    Check(monitors.Count >= 1, $"枚举到 {monitors.Count} 个显示器");
    foreach (var m in monitors) log.AppendLine($"     {m.DeviceName} {m.Bounds} primary={m.IsPrimary}");
    log.AppendLine($"     虚拟桌面: {vb}");
    Check(vb.Width > 0 && vb.Height > 0, $"虚拟桌面尺寸有效 {vb.Width}x{vb.Height}");
}
catch (Exception ex) { Fail("显示器枚举: " + ex.Message); }

Section("2. 全屏截图");
Bitmap? snap = null;
ScreenSnapshot? snapshot = null;
try
{
    var monitors = DisplayInfo.GetMonitors();
    var vb = DisplayInfo.GetVirtualBounds(monitors);
    var s = ScreenCapture.CaptureDesktop(vb);
    snap = s.Image;
    snapshot = s;    Check(snap != null && snap.Width > 0, $"截取虚拟桌面 {snap?.Width}x{snap?.Height}");
    Check(s.Windows.Count > 0, $"枚举到 {s.Windows.Count} 个可见窗口");

    // 验证像素非纯黑（说明真的抓到了内容）
    var c1 = snap!.GetPixel(10, 10);
    var c2 = snap.GetPixel(snap.Width / 2, snap.Height / 2);
    log.AppendLine($"     左上像素={c1} 中心像素={c2}");
    Check(c1.A == 255, "像素格式为不透明 32bpp");
}
catch (Exception ex) { Fail("全屏截图: " + ex.Message); }

Section("3. 区域裁剪");
try
{
    var region = new Rectangle(50, 50, 320, 180);
    var cropped = new Bitmap(320, 180, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(cropped))
        g.DrawImage(snapshot!.Image, new Rectangle(0, 0, 320, 180), region, GraphicsUnit.Pixel);
    Check(cropped.Width == 320 && cropped.Height == 180, $"裁剪尺寸 {cropped.Width}x{cropped.Height}");
    cropped.Dispose();
}
catch (Exception ex) { Fail("区域裁剪: " + ex.Message); }

Section("4. 标注模型渲染");
try
{
    using var canvas = new Bitmap(400, 300);
    using (var g = Graphics.FromImage(canvas)) { g.Clear(Color.FromArgb(250, 250, 252)); }

    var anns = new List<Annotation>
    {
        new RectAnnotation { Kind = AnnotationKind.Rectangle, Rect = new RectangleF(20, 20, 100, 60), Stroke = Color.Red, StrokeWidth = 3 },
        new EllipseAnnotation { Kind = AnnotationKind.Ellipse, Rect = new RectangleF(140, 20, 100, 60), Stroke = Color.Blue },
        new ArrowAnnotation { Kind = AnnotationKind.Arrow, Start = new PointF(40, 150), End = new PointF(200, 120), Stroke = Color.Green, StrokeWidth = 4 },
        new LineAnnotation { Kind = AnnotationKind.Line, Start = new PointF(220, 150), End = new PointF(360, 150), Stroke = Color.Purple },
        new PenAnnotation { Kind = AnnotationKind.Pen, Stroke = Color.Orange, StrokeWidth = 3 },
        new TextAnnotation { Kind = AnnotationKind.Text, Position = new PointF(20, 200), Text = "测试文字", Stroke = Color.Black, FontSize = 20 },
        new MosaicAnnotation { Kind = AnnotationKind.Mosaic, Rect = new RectangleF(20, 240, 120, 40), Source = snap, BlockSize = 10 },
        new BlurAnnotation { Kind = AnnotationKind.Blur, Rect = new RectangleF(160, 240, 120, 40), Source = snap, Radius = 6 },
        new NumberAnnotation { Kind = AnnotationKind.Number, Position = new PointF(320, 255), Index = 3, Stroke = Color.Teal },
    };
    ((PenAnnotation)anns[4]).Points.Add(new PointF(280, 200));
    ((PenAnnotation)anns[4]).Points.Add(new PointF(310, 225));
    ((PenAnnotation)anns[4]).Points.Add(new PointF(340, 195));

    using (var g = Graphics.FromImage(canvas))
    {
        Theme.SetupQuality(g);
        foreach (var a in anns) a.Draw(g);
    }
    Check(true, $"绘制 {anns.Count} 种标注成功");

    // 验证标注确实改变了像素（取矩形边框上的点）
    var px = canvas.GetPixel(20, 20);
    Check(px.R > 150 && px.G < 120, $"矩形标注生效（边框像素={px}）");

    // Clone 独立性
    var clone = anns[0].Clone();
    Check(clone is RectAnnotation && ((RectAnnotation)clone).Rect == ((RectAnnotation)anns[0]).Rect, "Clone 复制几何属性");
}
catch (Exception ex) { Fail("标注渲染: " + ex.Message); }

Section("5. 撤销/重做");
try
{
    var hist = new AnnotationHistory();
    var l1 = new List<Annotation>();
    hist.Push(l1);
    Check(!hist.CanUndo == false, "Push 后可撤销");
    l1.Add(new RectAnnotation { Kind = AnnotationKind.Rectangle });
    hist.Push(l1);
    Check(hist.Current.Count == 1, "历史记录 1 个标注");
    hist.Undo();
    Check(hist.Current.Count == 0, "撤销后回到 0 个");
    hist.Redo();
    Check(hist.Current.Count == 1, "重做后恢复 1 个");
}
catch (Exception ex) { Fail("撤销重做: " + ex.Message); }

Section("6. GIF 编码器（自研）");
try
{
    var frames = new List<Bitmap>();
    for (int i = 0; i < 6; i++)
    {
        var f = new Bitmap(120, 90);
        using (var g = Graphics.FromImage(f))
        {
            g.Clear(Color.FromArgb(240, 244, 250));
            g.FillEllipse(new SolidBrush(Color.FromArgb(94, 143, 246)), 10 + i * 15, 30, 40, 40);
            g.DrawString("F" + i, new Font("Arial", 14), Brushes.DarkSlateGray, 20, 60);
        }
        frames.Add(f);
    }

    var gifPath = Path.Combine(Path.GetTempPath(), "verify.gif");
    using (var fs = new FileStream(gifPath, FileMode.Create))
    {
        using var enc = new GifEncoder(fs, 120, 90);
        foreach (var f in frames) enc.AddFrame(f, 80);
        enc.Finish();
    }

    var len = new FileInfo(gifPath).Length;
    Check(len > 200, $"GIF 大小 {len} 字节");

    // 用 GDI+ 反读，验证结构合法且帧数正确
    using var loaded = Image.FromFile(gifPath);
    var dim = new FrameDimension(loaded.FrameDimensionsList[0]);
    int cnt = loaded.GetFrameCount(dim);
    Check(cnt == 6, $"帧数 {cnt}（应为 6）");
    Check(loaded.Width == 120 && loaded.Height == 90, $"尺寸 {loaded.Width}x{loaded.Height}");

    // 逐帧解码，验证 LZW 数据未损坏
    for (int i = 0; i < cnt; i++)
    {
        loaded.SelectActiveFrame(dim, i);
        using var bmp = new Bitmap(loaded);
        var px = bmp.GetPixel(30 + i * 15, 50);
        if (px.B < 200) { Fail($"第 {i} 帧像素异常 {px}"); break; }
    }
    Ok("6 帧全部解码成功且像素正确");

    foreach (var f in frames) f.Dispose();
}
catch (Exception ex) { Fail("GIF 编码: " + ex.Message); }

Section("7. OCR 识别");
try
{
    using var ocrBmp = new Bitmap(460, 120);
    using (var g = Graphics.FromImage(ocrBmp))
    {
        g.Clear(Color.White);
        using var font = new Font("Microsoft YaHei UI", 40, FontStyle.Bold);
        g.DrawString("截图识别 Test 2026", font, Brushes.Black, 12, 30);
    }
    var text = await OcrService.RecognizeAsync(ocrBmp);
    log.AppendLine($"     识别结果: [{text}]");
    Check(!string.IsNullOrWhiteSpace(text), "OCR 返回非空文本");
    Check(text.Contains("Test") || text.Contains("\u622a\u56fe"), "OCR 识别到预期内容");
    // 编码检查：不应出现替换字符
    Check(!text.Contains("\uFFFD"), "OCR 输出无乱码替换符");
}
catch (Exception ex) { Fail("OCR: " + ex.Message); }

Section("8. 文件名与保存");
try
{
    var name = ImageUtil.BuildFileName("截图_{date}_{time}", ".png");
    Check(name.EndsWith(".png") && name.Contains("截图"), $"文件名生成: {name}");

    using var bmp = new Bitmap(120, 90);
    using (var g = Graphics.FromImage(bmp)) g.Clear(Color.CornflowerBlue);
    var dir = Path.Combine(Path.GetTempPath(), "qqshot_verify");
    var path = ImageUtil.Save(bmp, dir, "test_{n}", ImageFormatKind.Png);
    Check(File.Exists(path), $"保存 PNG: {Path.GetFileName(path)} ({new FileInfo(path).Length} 字节)");

    var jpg = ImageUtil.Save(bmp, dir, "test_{n}", ImageFormatKind.Jpeg);
    Check(File.Exists(jpg), "保存 JPG");
}
catch (Exception ex) { Fail("文件保存: " + ex.Message); }

Section("9. 热键解析");
try
{
    Check(HotkeyManager.TryParse("Ctrl+Alt+A", out var m1, out var k1) && m1 != 0 && k1 == 'A', "解析 Ctrl+Alt+A");
    Check(HotkeyManager.TryParse("Ctrl+Shift+F2", out var m2, out var k2) && k2 == 0x71, "解析 Ctrl+Shift+F2");
    Check(!HotkeyManager.TryParse("乱写", out _, out _), "拒绝无效组合键");
}
catch (Exception ex) { Fail("热键解析: " + ex.Message); }

Section("10. 工具栏与图标绘制");
try
{
    using var tb = new Bitmap(400, 60);
    using (var g = Graphics.FromImage(tb))
    {
        g.Clear(Theme.Surface);
        Theme.SetupQuality(g);
        Theme.FillRounded(g, new RectangleF(4, 4, 392, 52), Theme.RadiusMedium, Theme.Surface, Theme.Border);
        int x = 14;
        foreach (IconKind k in Enum.GetValues<IconKind>())
        {
            Icons.Draw(g, k, new RectangleF(x, 14, 30, 30), Theme.Text);
            x += 34;
        }
    }
    Check(tb.GetPixel(20, 20).A > 0, "图标绘制产生像素");
    tb.Save(Path.Combine(Path.GetTempPath(), "qqshot_toolbar.png"), ImageFormat.Png);
    Ok("工具栏预览已导出");
}
catch (Exception ex) { Fail("工具栏绘制: " + ex.Message); }

Section("11. 译文原位替换");
try
{
    static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    static Rectangle InkBox(Bitmap bmp, Func<Color, bool> isInk)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = 0, maxY = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                if (!isInk(bmp.GetPixel(x, y))) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        if (maxX <= minX || maxY <= minY) return Rectangle.Empty;
        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    // 新译文统一用纯红绘制：这样"新墨迹"（红）与"原文残留"（黑/白）可以严格区分，
    // 从而能把**整框**（而不是只统计右半）都纳入残留检测。
    static bool IsRed(Color c) => c.R > 150 && c.G < 120 && c.B < 120;

    // 原文墨迹残留统计：整框外扩 expand 像素的范围内，属于原墨迹且不是红色译文的像素
    static int OldInkCount(Bitmap bmp, Rectangle box, Func<Color, bool> isInk, int expand = 8)
    {
        var r = Rectangle.Intersect(Rectangle.Inflate(box, expand, expand),
            new Rectangle(0, 0, bmp.Width, bmp.Height));
        int n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (IsRed(c)) continue;
                if (isInk(c)) n++;
            }
        return n;
    }

    static int RedCount(Bitmap bmp, Rectangle r)
    {
        int n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                if (IsRed(bmp.GetPixel(x, y))) n++;
        return n;
    }

    // 补丁四边逐像素扫描：外侧 1px 与内侧 1px 的色差，取最大值
    static int MaxEdgeDelta(Bitmap bmp, Rectangle patch)
    {
        var bounds = new Rectangle(0, 0, bmp.Width, bmp.Height);
        int max = 0;

        int Delta(int xo, int yo, int xi, int yi)
        {
            if (!bounds.Contains(xo, yo) || !bounds.Contains(xi, yi)) return 0;
            var o = bmp.GetPixel(xo, yo);
            var i = bmp.GetPixel(xi, yi);
            if (IsRed(i)) return 0;   // 内侧是译文笔画，无法比较背景
            return Math.Abs(o.R - i.R) + Math.Abs(o.G - i.G) + Math.Abs(o.B - i.B);
        }

        for (int x = patch.Left + 2; x < patch.Right - 2; x++)
        {
            max = Math.Max(max, Delta(x, patch.Top - 1, x, patch.Top));
            max = Math.Max(max, Delta(x, patch.Bottom, x, patch.Bottom - 1));
        }
        for (int y = patch.Top + 2; y < patch.Bottom - 2; y++)
        {
            max = Math.Max(max, Delta(patch.Left - 1, y, patch.Left, y));
            max = Math.Max(max, Delta(patch.Right, y, patch.Right - 1, y));
        }
        return max;
    }

    // 三类背景各验证一遍：浅色纯色 / 深色纯色 / 横向渐变。
    // 文本用带降部的 "Settings gyp"，且**故意把行框下沿截到基线**
    // （模拟 OCR 行框不含降部），以复现"降部残留"这类问题。
    void ReplaceCase(string name, Bitmap src, Func<Color, bool> isInk, int shrinkRight = 0)
    {
        var inkBox = InkBox(src, isInk);
        Check(!inkBox.IsEmpty, $"{name}：找到文字墨迹包围盒");
        if (inkBox.IsEmpty) return;

        // 把下沿截到约基线的位置（降部被切掉），模拟 OCR 的紧贴行框
        int cut = Math.Max(1, (int)Math.Round(inkBox.Height * 0.22));
        // shrinkRight > 0 时把右边界推进字形内部，让"行框右边缘那一列"正好落在
        // 抗锯齿像素上——这正是旧实现（2 点均值）产生整行灰带的机理。
        var box = Rectangle.FromLTRB(inkBox.Left, inkBox.Top, inkBox.Right - shrinkRight, inkBox.Bottom - cut);
        log.AppendLine($"     {name}：墨迹框 {inkBox} / OCR 行框 {box}");
        Check(box.Bottom < inkBox.Bottom, $"{name}：行框已截掉降部（{box.Bottom} < {inkBox.Bottom}）");

        var bd = TextBackdrop.Sample(src, box, 3, 2);
        Check(bd != null, $"{name}：采样成功");
        if (bd == null) return;

        // 补丁必须盖住行框外的降部墨迹
        Check(bd.Patch.Bottom > inkBox.Bottom, $"{name}：补丁覆盖降部（补丁底 {bd.Patch.Bottom} > 墨迹底 {inkBox.Bottom}）");

        // A. 自然配色版本，用于人眼复核（导出 PNG）
        var natural = new TranslationAnnotation
        {
            Kind = AnnotationKind.Text,
            Rect = box,
            Text = "确定替换",
            SamplePatch = bd.Patch,
            PadX = 3,
            PadY = 2,
            BackgroundColor = bd.MainBackground,
            TextColor = bd.TextColor,
            RowLeft = bd.RowLeft,
            RowRight = bd.RowRight
        };
        using (var vis = new Bitmap(src))
        {
            using (var g = Graphics.FromImage(vis)) natural.Draw(g);
            vis.Save(Path.Combine(Path.GetTempPath(), $"qqshot_translate_{name}.png"), ImageFormat.Png);
        }

        // B. 红色译文版本，用于严格断言
        var red = new TranslationAnnotation
        {
            Kind = AnnotationKind.Text,
            Rect = box,
            Text = "确定替换",
            SamplePatch = bd.Patch,
            PadX = 3,
            PadY = 2,
            BackgroundColor = bd.MainBackground,
            TextColor = Color.Red,
            RowLeft = bd.RowLeft,
            RowRight = bd.RowRight
        };
        using var outBmp = new Bitmap(src);
        using (var g = Graphics.FromImage(outBmp)) red.Draw(g);

        int before = OldInkCount(src, box, isInk);
        int after = OldInkCount(outBmp, box, isInk);
        Check(before > 200, $"{name}：原文墨迹 {before} 像素（整框±8px）");
        Check(after == 0, $"{name}：整框±8px 内无原文残留（{before} → {after}）");

        int redPixels = RedCount(outBmp, bd.Patch);
        Check(redPixels > 50, $"{name}：译文已绘制（{redPixels} 像素）");

        int delta = MaxEdgeDelta(outBmp, bd.Patch);
        Check(delta <= 3, $"{name}：补丁四边逐行扫描无色差（Δmax={delta}）");
    }

    // 1) 浅色纯色背景
    using (var light = new Bitmap(420, 90))
    {
        using (var g = Graphics.FromImage(light))
        {
            g.Clear(Color.White);
            using var f = new Font("Microsoft YaHei UI", 26, FontStyle.Regular);
            g.DrawString("Settings gyp", f, Brushes.Black, 12, 22);
        }
        ReplaceCase("white", light, c => Luma(c) < 110);
        // 复现 S1：行框右边界压在抗锯齿列上
        ReplaceCase("white-tight", light, c => Luma(c) < 110, shrinkRight: 2);
    }

    // 2) 深色背景
    using (var dark = new Bitmap(420, 90))
    {
        var bgColor = Color.FromArgb(28, 30, 36);
        using (var g = Graphics.FromImage(dark))
        {
            g.Clear(bgColor);
            using var f = new Font("Microsoft YaHei UI", 26, FontStyle.Regular);
            g.DrawString("Settings gyp", f, Brushes.White, 12, 22);
        }
        ReplaceCase("dark", dark, c => Luma(c) > 160);
    }

    // 3) 横向渐变背景
    using (var grad = new Bitmap(420, 90))
    {
        using (var g = Graphics.FromImage(grad))
        {
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, 420, 90),
                       Color.FromArgb(236, 244, 255), Color.FromArgb(120, 170, 240), 0f))
            {
                g.FillRectangle(lg, 0, 0, 420, 90);
            }
            using var f = new Font("Microsoft YaHei UI", 26, FontStyle.Regular);
            g.DrawString("Settings gyp", f, Brushes.Black, 12, 22);
        }
        ReplaceCase("gradient", grad, c => Luma(c) < 110);
    }
}
catch (Exception ex) { Fail("译文原位替换: " + ex.Message); }

snap?.Dispose();

var outPath = @"C:\Users\openwrt\WorkBuddy\2026-11-17-16-34-08\verify\verify.log";
File.WriteAllText(outPath, log.ToString());
Console.WriteLine(log.ToString());
