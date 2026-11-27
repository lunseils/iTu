using System.Drawing.Imaging;

namespace iTu.Core;

/// <summary>
/// 图像文件与剪贴板通用工具：文件名模板、目录/去重路径、编码选择、
/// 透明像素检测、剪贴板写入。截图成品落盘统一由 <see cref="ResultWriter"/> 负责，
/// 本类不再提供第二套保存实现，避免编码参数与 JPEG 质量策略分叉。
/// </summary>
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
            // 回退目录同样要真正创建，否则后续 FileStream(FileMode.Create) 会抛
            // DirectoryNotFoundException，保存链路在误配置下永久静默失败。
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "iTu");
            try { Directory.CreateDirectory(fallback); }
            catch (Exception fb) { Log.Info($"[io] 回退目录也无法创建：{fb.Message}"); }
            return fallback;
        }
    }

    /// <summary>
    /// 原子创建一个不冲突的可写文件：用 <see cref="FileMode.CreateNew"/> 抢占，
    /// 消除「先 File.Exists 探测、再 FileMode.Create」之间的 TOCTOU 窗口。
    /// 成功时返回已打开的流与最终路径，调用方负责释放流；只有该路径才允许
    /// 在后续编码失败时删除，避免并发会话互相删掉对方刚写好的图片。
    /// </summary>
    public static FileStream CreateUniqueFile(string basePath, out string actualPath)
    {
        var dir = Path.GetDirectoryName(basePath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        for (int i = 1; i < 10000; i++)
        {
            var candidate = i == 1 ? basePath : Path.Combine(dir, $"{stem}_{i}{ext}");
            try
            {
                var fs = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write);
                actualPath = candidate;
                return fs;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // 被并发会话抢占，换下一个候选名
            }
        }
        var fallback = Path.Combine(dir, $"{stem}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}");
        actualPath = fallback;
        return new FileStream(fallback, FileMode.CreateNew, FileAccess.Write);
    }

    /// <summary>位图是否含透明像素（alpha &lt; 255）。</summary>
    public static bool HasTransparency(Bitmap bmp)
    {
        var fmt = bmp.PixelFormat;
        if (fmt is System.Drawing.Imaging.PixelFormat.Format32bppArgb
            or System.Drawing.Imaging.PixelFormat.Format32bppPArgb)
        {
            // 按位图自身的格式锁定：PArgb 用 Argb 锁定会触发隐式 unpremultiply，
            // 既多一次全图转换，也可能让 GDI+ 拒绝该组合而抛异常。
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, fmt);
            try
            {
                int stride = data.Stride;
                int w = bmp.Width, h = bmp.Height;
                // 抽检 alpha 字节（BGRA 的 +3 偏移）。步长 16 会漏掉小圆角、
                // 阴影羽化带等窄边透明区；改为步长 4 并补扫最后一行/列。
                for (int y = 0; y < h; y += 4)
                {
                    IntPtr row = data.Scan0 + y * stride;
                    for (int x = 0; x < w; x += 4)
                        if (System.Runtime.InteropServices.Marshal.ReadByte(row, x * 4 + 3) < 255)
                            return true;
                }
                IntPtr lastRow = data.Scan0 + (h - 1) * stride;
                for (int x = 0; x < w; x++)
                    if (System.Runtime.InteropServices.Marshal.ReadByte(lastRow, x * 4 + 3) < 255)
                        return true;
                for (int y = 0; y < h; y++)
                    if (System.Runtime.InteropServices.Marshal.ReadByte(data.Scan0 + y * stride, (w - 1) * 4 + 3) < 255)
                        return true;
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
