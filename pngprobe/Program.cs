using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using iTu.Core;

class P
{
    static int Main()
    {
        var dir = Path.Combine(Path.GetTempPath(), "itu_save_verify");
        Directory.CreateDirectory(dir);
        int fail = 0;

        // A) 单线程四格式
        foreach (var fmt in new[] { ImageFormatKind.Png, ImageFormatKind.Jpeg, ImageFormatKind.Bmp })
        {
            var settings = new AppSettings { SaveDirectory = dir, ImageFormat = fmt,
                                           FileNamePattern = "单线程_{date}_{time}_{n}" };
            using var bmp = new Bitmap(120, 80);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.CornflowerBlue);
            fail += Check($"{fmt} 不透明", settings, bmp, ".png/.jpg/.bmp");
        }
        var ts = new AppSettings { SaveDirectory = dir, ImageFormat = ImageFormatKind.Jpeg,
                                  FileNamePattern = "透明_{date}_{time}_{n}" };
        using (var tr = new Bitmap(120, 80, PixelFormat.Format32bppArgb))
        {
            using var g = Graphics.FromImage(tr);
            g.Clear(Color.Transparent);
            g.FillEllipse(Brushes.Red, 10, 10, 100, 60);
            fail += Check("透明图强制 PNG", ts, tr, ".png");
        }

        // B) 并发抢占：12 个任务同秒保存，验证不重名、不误删、产物均可打开
        var paths = new ConcurrentBag<string>();
        var results = new ConcurrentBag<bool>();
        var cdir = Path.Combine(dir, "concurrent");
        Directory.CreateDirectory(cdir);
        // 清掉上一轮残留
        foreach (var f in Directory.GetFiles(cdir, "*.png")) { try { File.Delete(f); } catch { } }
        var cs = new AppSettings { SaveDirectory = cdir, ImageFormat = ImageFormatKind.Png,
                                   FileNamePattern = "并发_{date}_{time}" };
        Parallel.For(0, 12, i =>
        {
            using var bmp = new Bitmap(64, 48);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(30 * i % 255, 80, 160));
            bool ok = ResultWriter.TrySave(bmp, cs, out var p);
            results.Add(ok);
            if (ok) paths.Add(p);
        });
        var arr = paths.ToArray();
        bool allOk = results.All(x => x) && arr.Length == 12 && arr.Distinct().Count() == 12;
        foreach (var p in arr)
        {
            var fi = new FileInfo(p);
            if (!fi.Exists || fi.Length == 0) { allOk = false; break; }
            try { using var im = Image.FromFile(p); if (im.Width != 64) allOk = false; }
            catch { allOk = false; }
        }
        Console.WriteLine($"[并发抢占] {(allOk ? "PASS" : "FAIL")} 成功={results.Count(x => x)} 唯一路径={arr.Distinct().Count()}/12 实际文件={Directory.GetFiles(cdir, "*.png").Length}");
        if (!allOk) fail++;

        Console.WriteLine(fail == 0 ? "ALL PASS" : $"FAILED={fail}");
        return fail;
    }

    static int Check(string name, AppSettings settings, Bitmap bmp, string expectExt)
    {
        bool ok = ResultWriter.TrySave(bmp, settings, out var path);
        if (!ok) { Console.WriteLine($"[{name}] TrySave 返回 false"); return 1; }
        var bytes = File.ReadAllBytes(path);
        string head = bytes.Length >= 8 ? BitConverter.ToString(bytes, 0, 8) : "(不足 8 字节)";
        bool extOk = expectExt.Contains(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase);
        bool reopen = false;
        try { using var im = Image.FromFile(path); reopen = im.Width == 120 && im.Height == 80; }
        catch (Exception ex) { Console.WriteLine($"[{name}] 重新打开失败: {ex.Message}"); }
        bool pass = bytes.Length > 0 && reopen && extOk;
        Console.WriteLine($"[{name}] {(pass ? "PASS" : "FAIL")} size={bytes.Length} head={head} ext={Path.GetExtension(path)} reopen={reopen}");
        return pass ? 0 : 1;
    }
}
