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

    /// <summary>提交一次修改（调用前先 Undo 前状态已入栈）。</summary>
    public void Push(IReadOnlyList<Annotation> next)
    {
        _undo.Add(Current.Select(a => a.Clone()).ToList());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        Current = next.Select(a => a.Clone()).ToList();
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        _redo.Add(Current.Select(a => a.Clone()).ToList());
        Current = _undo[^1].Select(a => a.Clone()).ToList();
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _undo.Add(Current.Select(a => a.Clone()).ToList());
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
        var now = DateTime.Now;
        var name = pattern
            .Replace("{date}", now.ToString("yyyyMMdd"))
            .Replace("{time}", now.ToString("HHmmss"))
            .Replace("{n}", now.ToString("fff"));

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        if (string.IsNullOrWhiteSpace(name)) name = "截图_" + now.ToString("yyyyMMdd_HHmmss");
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
