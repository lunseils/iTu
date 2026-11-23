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

            path = ImageUtil.EnsureUniquePath(
                Path.Combine(dir, ImageUtil.BuildFileName(settings.FileNamePattern, ext)));

            // 与 ImageUtil.Save 保持一致：JPEG 用 92 质量编码器，避免默认质量(~75)导致的画质差异。
            var codec = ImageUtil.SelectCodec(transparent
                ? ImageFormatKind.Png
                : settings.ImageFormat);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var enc = new EncoderParameters(1))
            {
                if (!transparent && settings.ImageFormat == ImageFormatKind.Jpeg)
                    enc.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
                image.Save(fs, codec, enc);
            }

            Log.Info($"[save] {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("[save] 保存失败: " + ex.Message);
            return false;
        }
    }
}
