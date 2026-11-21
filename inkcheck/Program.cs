using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using QqShot.Core;
using QqShot.Ui;

int pass = 0, fail = 0;
void Check(bool ok, string name, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"[OK]   {name} {detail}"); }
    else { fail++; Console.WriteLine($"[FAIL] {name} {detail}"); }
}
void Section(string s) => Console.WriteLine($"\n=== {s} ===");

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

Section("1. 显示器枚举（MONITORINFOEX Unicode 布局）");
try
{
    var monitors = DisplayInfo.GetMonitors();
    Check(monitors.Count >= 1, "枚举到显示器", $"count={monitors.Count}");
    Check(monitors.All(m => !string.IsNullOrWhiteSpace(m.DeviceName)),
        "设备名非乱码", string.Join(" | ", monitors.Select(m => m.DeviceName)));
    var vb = DisplayInfo.GetVirtualBounds(monitors);
    Check(vb.Width > 0 && vb.Height > 0, "虚拟桌面有效", vb.ToString());
}
catch (Exception ex) { Check(false, "显示器枚举异常", ex.Message); }

Section("2. 圆角路径大半径不溢出");
try
{
    var mi = typeof(RectAnnotation).GetMethod("Rounded", BindingFlags.Static | BindingFlags.NonPublic)!;
    using var path = (System.Drawing.Drawing2D.GraphicsPath)mi.Invoke(null,
        new object[] { new RectangleF(0, 0, 40, 20), 500f })!;
    var bounds = path.GetBounds();
    Check(bounds.X >= -0.01f && bounds.Y >= -0.01f &&
          bounds.Right <= 40.01f && bounds.Bottom <= 20.01f,
        "超大半径路径仍在矩形内", bounds.ToString());
}
catch (Exception ex) { Check(false, "圆角路径异常", ex.Message); }

Section("3. GIF delay 单位（毫秒 → 1/100 秒）");
try
{
    using var ms = new MemoryStream();
    using (var enc = new GifEncoder(ms, 4, 4))
    using (var frame = new Bitmap(4, 4))
    {
        frame.SetPixel(0, 0, Color.Red);
        enc.AddFrame(frame, 100);   // 100ms 应为 10 centiseconds
        enc.Finish();
    }
    var bytes = ms.ToArray();
    int idx = -1;
    for (int i = 0; i < bytes.Length - 4; i++)
        if (bytes[i] == 0x21 && bytes[i + 1] == 0xF9 && bytes[i + 2] == 0x04) { idx = i; break; }
    Check(idx >= 0, "找到 GCE 块", $"offset={idx}");
    if (idx >= 0)
    {
        int cs = bytes[idx + 4] | (bytes[idx + 5] << 8);
        Check(cs == 10, "100ms 编码为 10cs", $"实际={cs}");
    }
}
catch (Exception ex) { Check(false, "GIF 编码异常", ex.Message); }

Section("4. 同秒连续保存不覆盖");
try
{
    var dir = Path.Combine(Path.GetTempPath(), "itu_unique_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var p1 = ImageUtil.EnsureUniquePath(Path.Combine(dir, "截图.png"));
    File.WriteAllText(p1, "a");
    var p2 = ImageUtil.EnsureUniquePath(Path.Combine(dir, "截图.png"));
    Check(p1 != p2, "生成不同文件名", $"{Path.GetFileName(p1)} / {Path.GetFileName(p2)}");
    Directory.Delete(dir, true);
}
catch (Exception ex) { Check(false, "唯一文件名异常", ex.Message); }

Section("5. 边中点控制点（两侧扩拉线已取消）");
try
{
    var sel = new Rectangle(100, 100, 300, 200);
    var topMid = new Point(250, 100);
    Check(OverlaySession.GripAt(sel, topMid) == OverlaySession.Grip.T,
        "上边中点命中上边", $"实际={OverlaySession.GripAt(sel, topMid)}");
    Check(OverlaySession.GripAt(sel, new Point(100, 200)) == OverlaySession.Grip.L,
        "左边中点命中左边", $"实际={OverlaySession.GripAt(sel, new Point(100, 200))}");
    Check(OverlaySession.GripAt(sel, new Point(250, 250)) == OverlaySession.Grip.Move,
        "选区内部是整体移动", $"实际={OverlaySession.GripAt(sel, new Point(250, 250))}");

    // 边框外侧 16px 处以前是"扩拉线"的命中区，现在必须什么都不命中，
    // 否则在选区旁边随手一按就会意外拉边（控制点本身的容差只有 9px）
    Check(OverlaySession.GripAt(sel, new Point(250, 84)) == OverlaySession.Grip.None,
        "边框外不再命中任何把手", $"实际={OverlaySession.GripAt(sel, new Point(250, 84))}");
}
catch (Exception ex) { Check(false, "控制点命中异常", ex.Message); }

Section("6. 预览标注 offset = 窗口原点（截图所见即所得）");
try
{
    using var canvas = new Bitmap(200, 100, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(canvas))
    {
        g.Clear(Color.White);
        // 标注存屏幕坐标 (150,60)，窗口原点是 (100,50) → 应画在客户区 (50,10)
        var a = new RectAnnotation
        {
            Kind = AnnotationKind.Rectangle,
            Rect = new RectangleF(150, 60, 20, 20),
            Stroke = Color.Red,
            StrokeWidth = 2
        };
        OverlayInk.Draw(g, new[] { a }, new PointF(100, 50));
    }
    var hit = canvas.GetPixel(50, 10);
    Check(hit.R > 180 && hit.G < 100, "标注落在正确客户区坐标", $"pixel={hit}");
}
catch (Exception ex) { Check(false, "标注预览异常", ex.Message); }

Section("7. 马赛克 SourceOrigin（负坐标虚拟桌面）");
try
{
    using var src = new Bitmap(100, 100, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(src))
    {
        g.Clear(Color.Black);
        using var b = new SolidBrush(Color.Lime);
        g.FillRectangle(b, new Rectangle(0, 0, 50, 50));
    }
    // 源图原点在屏幕坐标 (-1920,-100)：屏幕区域 (-1920,-100,30,30)
    // 对应源图 (0,0,30,30) 的绿色
    using var canvas = new Bitmap(40, 40, PixelFormat.Format32bppArgb);
    var m = new MosaicAnnotation
    {
        Kind = AnnotationKind.Mosaic,
        Rect = new RectangleF(-1920, -100, 30, 30),
        Source = src,
        SourceOrigin = new Point(-1920, -100),
        BlockSize = 6
    };
    using (var g = Graphics.FromImage(canvas)) OverlayInk.Draw(g, new[] { m }, PointF.Empty);
    // 用负坐标绘制会被裁掉，改为手工合成到图片坐标验证采样
    using var outBmp = new Bitmap(30, 30, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(outBmp))
    {
        // 把负屏幕坐标平移回图片左上角，便于取像素
        g.TranslateTransform(1920, 100);
        m.Draw(g);
    }
    var px = outBmp.GetPixel(4, 4);
    Check(px.G > 180 && px.R < 100, "按 SourceOrigin 取到正确区域", $"pixel={px}");
    m.ReleaseCachedResources();
}
catch (Exception ex) { Check(false, "马赛克坐标异常", ex.Message); }

Section("8. 文字工具 Esc 取消不写回结果");
try
{
    string? result = "<未触发 FormClosed>";
    using var f = new TextInputForm(new Point(80, 80), Color.Red, 16);
    f.FormClosed += (_, _) => result = f.Result ?? "<null>";
    f.Show();
    Application.DoEvents();

    // 不能依赖 SendKeys：Console 程序的控制台窗口通常才是前台窗口，
    // 按键会发给控制台，用例会"稳定"地失败。这里把 WM_KEYDOWN 直接投给
    // 真正获得焦点的那个 TextBox，路径与真人按 Esc 完全一致（走 KeyDown 分支）。
    var target = f.ActiveControl ?? (Control)f;
    KeyPost.PostMessage(target.Handle, 0x0100 /*WM_KEYDOWN*/, (IntPtr)0x1B, IntPtr.Zero);
    KeyPost.PostMessage(target.Handle, 0x0101 /*WM_KEYUP*/, (IntPtr)0x1B, IntPtr.Zero);

    for (int i = 0; i < 40 && result == "<未触发 FormClosed>"; i++)
    {
        Application.DoEvents();
        Thread.Sleep(25);
    }
    Check(result == "<null>", "Esc 后 Result 为空", $"实际={result}");
}
catch (Exception ex) { Check(false, "文字取消异常", ex.Message); }


Console.WriteLine($"\n总计: {pass} 通过 / {fail} 失败");
Environment.ExitCode = fail == 0 ? 0 : 1;

/// <summary>只给第 8 节用：把按键直接投给焦点控件（不受前台窗口影响）。</summary>
static class KeyPost
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
