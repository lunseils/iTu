using System.Text;

namespace QaCheck;

internal static class Entry
{
    [STAThread]
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        W.SetProcessDpiAwarenessContext(W.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string group = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

        if (group == "px" && args.Length >= 7)
        {
            Px(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6]));
            return;
        }

        if (group == "zoom" && args.Length >= 8)
        {
            Zoom(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]),
                 int.Parse(args[5]), int.Parse(args[6]), int.Parse(args[7]));
            return;
        }

        var rep = new Report();
        var stamp = DateTime.Now.ToString("MMdd_HHmmss");
        var outPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", $"qa_{group}_{stamp}.log");
        outPath = Path.GetFullPath(outPath);

        try
        {
            if (group is "inproc" or "all")
            {
                // 位图/GDI 操作放到 MTA 线程，避免与 STA 消息泵纠缠
                Task.Run(() =>
                {
                    InProc.CaptureTests(rep);
                    InProc.SamplerTests(rep);
                    InProc.TranslateTests(rep);
                }).GetAwaiter().GetResult();
            }

            if (group is "gui" or "all")
            {
                Gui.Run(rep);
            }

            if (group is "drag") { Gui.Run(rep, dragOnly: true); }

            if (group is "risk")
            {
                Task.Run(() =>
                {
                    Round2.TightSpacing(rep);
                    Round2.ImageEdges(rep);
                    Round2.ShortLines(rep);
                    Round2.NullPatchFallback(rep);
                    Round2.SnapAlgorithm(rep);
                }).GetAwaiter().GetResult();
            }

            if (group is "r2gui") { Gui.RunR2Only(rep); }

            if (group is "enum")
            {
                Task.Run(() => Round2.DumpSnapshotWindows(rep)).GetAwaiter().GetResult();
            }

            if (group is "smoke")
            {
                Gui.RunSmokeOnly(rep);
            }

            if (group is "translate")
            {
                Task.Run(() => InProc.TranslateTests(rep)).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            rep.Fail_("测试进程异常: " + ex);
        }

        rep.Save(outPath);
        Console.WriteLine("LOG: " + outPath);
    }

    /// <summary>打印指定区域内哪些像素"非背景"，用于定位残留墨迹。</summary>
    private static void Px(string src, string baseline, int x0, int y0, int w, int h)
    {
        using var a = new Bitmap(src);
        using var b = new Bitmap(baseline);
        for (int y = y0; y < y0 + h && y < a.Height; y++)
        {
            var sb = new System.Text.StringBuilder($"y={y}: ");
            int n = 0;
            for (int x = x0; x < x0 + w && x < a.Width; x++)
            {
                var ca = a.GetPixel(x, y);
                var cb = b.GetPixel(x, y);
                bool diff = Math.Abs(ca.R - cb.R) > 2 || Math.Abs(ca.G - cb.G) > 2 || Math.Abs(ca.B - cb.B) > 2;
                if (diff) { sb.Append($"({x}:{ca.R},{ca.G},{ca.B} vs {cb.R},{cb.G},{cb.B}) "); n++; }
            }
            if (n > 0) Console.WriteLine(sb.ToString());
        }
    }

    /// <summary>裁剪并整数放大小块区域，便于人眼复核细节。</summary>
    private static void Zoom(string src, string dst, int x, int y, int w, int h, int scale)
    {
        using var b = new Bitmap(src);
        var rect = new Rectangle(x, y, Math.Min(w, b.Width - x), Math.Min(h, b.Height - y));
        using var crop = b.Clone(rect, b.PixelFormat);
        using var zoom = new Bitmap(rect.Width * scale, rect.Height * scale);
        using (var g = Graphics.FromImage(zoom))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(crop, 0, 0, zoom.Width, zoom.Height);
        }
        zoom.Save(dst, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("ZOOM: " + dst);
    }
}
