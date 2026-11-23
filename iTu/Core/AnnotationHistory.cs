using System.Drawing.Imaging;

namespace iTu.Core;

/// <summary>撤销 / 重做栈：保存标注快照，交互简单可靠。</summary>
public sealed class AnnotationHistory
{
    private readonly List<List<Annotation>> _undo = new();
    private readonly List<List<Annotation>> _redo = new();

    public IReadOnlyList<Annotation> Current { get; private set; } = new List<Annotation>();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>释放一组标注的懒加载 GDI 缓存（Clone 不共享这些缓存，可安全释放）。</summary>
    private static void Release(IEnumerable<Annotation> items)
    {
        foreach (var a in items) a.ReleaseCachedResources();
    }

    /// <summary>提交一次修改（调用前先 Undo 前状态已入栈）。</summary>
    public void Push(IReadOnlyList<Annotation> next)
    {
        _undo.Add(Current.Select(a => a.Clone()).ToList());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        Release(Current);   // 旧 Current 已 Clone 入栈，其 GDI 缓存不再需要
        Current = next.Select(a => a.Clone()).ToList();
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        _redo.Add(Current.Select(a => a.Clone()).ToList());
        Release(Current);
        Current = _undo[^1].Select(a => a.Clone()).ToList();
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _undo.Add(Current.Select(a => a.Clone()).ToList());
        Release(Current);
        Current = _redo[^1].Select(a => a.Clone()).ToList();
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }
}

public static class ImageUtil
{
    /// <summary>按模板生成文件名，支持 {date} {time} {n}。</summary>
    public static string BuildFileName(string pattern, string extension)
    {
        // settings.json 被手动改成 null 时不要抛 NRE（外层虽会 catch，但这里直接兜底更稳）。
        pattern ??= "截图_{date}_{time}";
        var now = DateTime.Now;
        var name = pattern
            .Replace("{date}", now.ToString("yyyyMMdd"))
            .Replace("{time}", now.ToString("HHmmss"))
            .Replace("{n}", now.ToString("fff"));

        // 过滤文件系统非法字符，并额外移除路径分隔符/控制字符，确保模板无法
        // 拼出 "..\" 之类跳出保存目录的路径（用户可在设置里自定义模板）。
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Replace('/', '_').Replace('\\', '_');
        name = new string(name.Select(ch => char.IsControl(ch) ? '_' : ch).ToArray());
        name = name.Trim(' ', '.');

        if (string.IsNullOrWhiteSpace(name)) name = "截图_" + now.ToString("yyyyMMdd_HHmmss");
        if (name.Length > 120) name = name[..120];
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name += extension;
        return name;
    }

    /// <summary>
    /// 避免同秒/同毫秒连续保存互相覆盖：存在同名文件时追加 _2、_3 …
    /// 不改变文件名模板与扩展名，因此对调用方完全透明。
    /// </summary>
    public static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}");
    }

    public static string EnsureDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            Log.Info($"[io] 无法创建目录 {dir}，回退到「图片\\iTu」：{ex.Message}");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iTu");
        }
    }

    public static string Save(Bitmap bmp, string directory, string pattern, ImageFormatKind format)
    {
        var dir = EnsureDirectory(directory);

        // 圆角/阴影图含透明像素：强制改存 PNG 保留透明四角，
        // 避免 JPEG/BMP 把透明区编码成黑角。
        if (HasTransparency(bmp)) format = ImageFormatKind.Png;

        var ext = format switch
        {
            ImageFormatKind.Jpeg => ".jpg",
            ImageFormatKind.Bmp => ".bmp",
            _ => ".png"
        };

        var path = EnsureUniquePath(Path.Combine(dir, BuildFileName(pattern, ext)));
        var codec = SelectCodec(format);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

        if (format == ImageFormatKind.Jpeg)
        {
            using var enc = new EncoderParameters(1);
            enc.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
            bmp.Save(fs, codec, enc);
        }
        else
        {
            bmp.Save(fs, codec, null);
        }
        return path;
    }

    /// <summary>保存到用户指定的完整路径（扩展名决定编码格式）。</summary>
    public static void SaveTo(Bitmap bmp, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormatKind.Jpeg,
            ".bmp" => ImageFormatKind.Bmp,
            _ => ImageFormatKind.Png
        };

        // 圆角/阴影图含透明像素：用户指定了 jpg/bmp 后缀时改回 png 后缀，
        // 保留透明四角，避免编码成黑角。
        if (HasTransparency(bmp) && format != ImageFormatKind.Png)
        {
            path = Path.ChangeExtension(path, ".png");
            format = ImageFormatKind.Png;
        }

        var codec = SelectCodec(format);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);

        if (format == ImageFormatKind.Jpeg)
        {
            using var enc = new EncoderParameters(1);
            enc.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
            bmp.Save(fs, codec, enc);
        }
        else
        {
            bmp.Save(fs, codec, null);
        }
    }

    /// <summary>位图是否含透明像素（alpha &lt; 255）。</summary>
    public static bool HasTransparency(Bitmap bmp)
    {
        if (bmp.PixelFormat is System.Drawing.Imaging.PixelFormat.Format32bppArgb
            or System.Drawing.Imaging.PixelFormat.Format32bppPArgb)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                int h = bmp.Height;
                // 抽检 alpha 字节（BGRA 的 +3 偏移），步长 16 足够发现大片透明角
                for (int y = 0; y < h; y += 16)
                {
                    IntPtr row = data.Scan0 + y * stride;
                    for (int x = 0; x < bmp.Width; x += 16)
                    {
                        if (System.Runtime.InteropServices.Marshal.ReadByte(row, x * 4 + 3) < 255)
                            return true;
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        return false;
    }

    public static ImageCodecInfo SelectCodec(ImageFormatKind format) => format switch
    {
        ImageFormatKind.Jpeg => ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid),
        ImageFormatKind.Bmp => ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Bmp.Guid),
        _ => ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Png.Guid)
    };

    /// <summary>复制到剪贴板（失败时静默重试一次）。</summary>
    public static void CopyToClipboard(Bitmap bmp)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetImage(bmp);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(60);
            }
        }
    }
}
