using System.Drawing.Imaging;

namespace iTu.Core;

/// <summary>
/// 把成品图直接写到设置里的保存目录（截图界面的"保存"就是它）。
/// </summary>
public static class ResultWriter
{
    public static bool TrySave(Bitmap image, AppSettings settings, out string path)
    {
        path = string.Empty;
        // 只有「本次 CreateNew 成功抢占」的路径才允许在异常时删除。
        // 若用 File.Exists 探测出的路径，可能被并发会话抢先创建并写盘，
        // 此时无条件删除会毁掉对方刚保存好的图片。
        string? owned = null;

        try
        {
            var dir = ImageUtil.EnsureDirectory(settings.SaveDirectory);

            // 圆角/阴影图含透明像素，JPEG/BMP 会把透明区编码成黑角。
            // 遇到透明图一律改存 PNG，保留透明四角。
            bool transparent = ImageUtil.HasTransparency(image);
            var ext = transparent
                ? ".png"
                : settings.ImageFormat switch
                {
                    ImageFormatKind.Jpeg => ".jpg",
                    ImageFormatKind.Bmp => ".bmp",
                    _ => ".png"
                };

            var basePath = Path.Combine(dir,
                ImageUtil.BuildFileName(settings.FileNamePattern, ext));

            // JPEG 用 92 质量编码器（默认质量约 75）；PNG/BMP 不需要编码器参数。
            bool jpeg = !transparent && settings.ImageFormat == ImageFormatKind.Jpeg;
            var codec = ImageUtil.SelectCodec(transparent
                ? ImageFormatKind.Png
                : settings.ImageFormat);

            using (var fs = ImageUtil.CreateUniqueFile(basePath, out var actual))
            {
                owned = actual;
                if (jpeg)
                {
                    using var enc = new EncoderParameters(1);
                    enc.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
                    image.Save(fs, codec, enc);
                }
                else
                {
                    // PNG/BMP 必须传 null 编码器参数：EncoderParameters(1) 且 Param[0] 为 null
                    // 会让 GDI+ 抛 NullReferenceException，而 FileStream 已先创建了文件，
                    // 结果就是一个 0 字节、无 PNG 签名的损坏文件（0xbad20009）。
                    image.Save(fs, codec, null);
                }
            }

            path = owned;
            Log.Info($"[save] {path}");
            return true;
        }
        catch (Exception ex)
        {
            // 编码中途失败会留下不完整/空文件，只清理本次自己创建的那一个。
            if (owned != null)
            {
                try { File.Delete(owned); } catch { /* 目录只读或文件被占用时忽略 */ }
            }
            path = string.Empty;
            Log.Info("[save] 保存失败: " + ex.Message);
            return false;
        }
    }
}
