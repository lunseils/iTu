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
            var (ext, format) = transparent
                ? (".png", ImageFormat.Png)
                : settings.ImageFormat switch
                {
                    ImageFormatKind.Jpeg => (".jpg", ImageFormat.Jpeg),
                    ImageFormatKind.Bmp => (".bmp", ImageFormat.Bmp),
                    _ => (".png", ImageFormat.Png)
                };

            path = ImageUtil.EnsureUniquePath(
                Path.Combine(dir, ImageUtil.BuildFileName(settings.FileNamePattern, ext)));

            image.Save(path, format);

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
