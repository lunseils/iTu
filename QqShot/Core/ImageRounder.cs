using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QqShot.Core;

/// <summary>
/// 圆角裁剪：抗锯齿 + 边缘羽化。
///
/// 之前的实现用 <c>Graphics.SetClip(path)</c> —— 剪裁区是 **1 位掩码**，
/// 边缘只能是硬像素，圆角必然出现台阶；换 <c>FillPath</c> 后有了抗锯齿，
/// 但边缘仍是一条"数学上精确"的曲线，缩放到小尺寸时看着还是发干。
///
/// 这里改成 **alpha 掩码 + 盒式模糊**：
///   1. 画一张圆角白块当掩码（抗锯齿绘制）；
///   2. 对掩码做小半径模糊（羽化，默认 1.2px）；
///   3. 用掩码乘到原图的 alpha 上。
/// 结果是一圈柔和的过渡带，边缘既锐利又自然，没有台阶感。
/// </summary>
public static class ImageRounder
{
    /// <summary>把图像裁成圆角，四角（及边缘羽化带）为透明。</summary>
    /// <param name="src">源图；radius &gt; 0 时会被释放，radius = 0 时原样返回、不释放。</param>
    /// <param name="radius">圆角半径（像素）。</param>
    /// <param name="feather">羽化半径（像素，0 表示只做抗锯齿）。</param>
    public static Bitmap Round(Bitmap src, int radius, float feather = 1.2f)
    {
        int w = src.Width, h = src.Height;
        int r = Math.Clamp(radius, 0, Math.Min(w, h) / 2);
        if (r <= 0) return src;

        using var mask = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(mask))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float d = r * 2f;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(w - d, 0, d, d, 270, 90);
            path.AddArc(w - d, h - d, d, d, 0, 90);
            path.AddArc(0, h - d, d, d, 90, 90);
            path.CloseFigure();

            using var brush = new SolidBrush(Color.White);
            g.FillPath(brush, path);
        }

        byte[] alpha = ExtractAlpha(mask);
        if (feather > 0.05f) BoxBlur(alpha, w, h, Math.Max(1, (int)MathF.Round(feather)));

        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
            g.DrawImageUnscaled(src, 0, 0);

        ApplyAlpha(dst, alpha);
        src.Dispose();
        return dst;
    }

    /// <summary>
    /// 给圆角图加一层柔和投影（参考图里的"阴影"勾选）。
    /// 做法：取成品图的 alpha 当形状掩码 → 下偏移 + 模糊 → 作为黑色蒙版的 alpha → 叠在原图下方。
    /// </summary>
    public static Bitmap AddShadow(Bitmap rounded, int radius, int pad = 20, int offsetY = 6)
    {
        int w = rounded.Width, h = rounded.Height;
        var canvas = new Bitmap(w + pad * 2, h + pad * 2, PixelFormat.Format32bppArgb);

        try
        {
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // 1) 形状掩码 → 模糊 → 半透明黑
                var mask = ExtractAlpha(rounded);
                BoxBlur(mask, w, h, Math.Max(2, radius / 6));
                BoxBlur(mask, w, h, 6);

                using (var shadow = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    var data = shadow.LockBits(new Rectangle(0, 0, w, h),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    var buf = new byte[data.Stride * h];
                    try
                    {
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int i = y * data.Stride + x * 4;
                                int a = (int)(mask[y * w + x] * 0.32f);
                                buf[i] = 0; buf[i + 1] = 0; buf[i + 2] = 0; buf[i + 3] = (byte)a;
                            }
                        System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
                    }
                    finally
                    {
                        shadow.UnlockBits(data);
                    }

                    g.DrawImageUnscaled(shadow, pad, pad + offsetY);
                }

                // 2) 原图压在上面
                g.DrawImageUnscaled(rounded, pad, pad);
            }
        }
        catch
        {
            canvas.Dispose();
            rounded.Dispose();
            throw;
        }

        rounded.Dispose();
        return canvas;
    }

    private static byte[] ExtractAlpha(Bitmap mask)
    {
        var data = mask.LockBits(new Rectangle(0, 0, mask.Width, mask.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var buf = new byte[data.Stride * mask.Height];
        try { System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length); }
        finally { mask.UnlockBits(data); }

        var alpha = new byte[mask.Width * mask.Height];
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
                alpha[y * mask.Width + x] = buf[y * data.Stride + x * 4 + 3];   // BGRA 的 A

        return alpha;
    }

    private static void ApplyAlpha(Bitmap bmp, byte[] alpha)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[data.Stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    buf[y * data.Stride + x * 4 + 3] = alpha[y * bmp.Width + x];

            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>可分离盒式模糊（横向 + 纵向），对 alpha 数组原地处理。</summary>
    private static void BoxBlur(byte[] src, int w, int h, int radius)
    {
        var tmp = new byte[src.Length];
        int span = radius * 2 + 1;

        // 横向
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int sum = 0;

            for (int x = -radius; x <= radius; x++)
                sum += src[row + Math.Clamp(x, 0, w - 1)];

            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = (byte)(sum / span);
                sum -= src[row + Math.Clamp(x - radius, 0, w - 1)];
                sum += src[row + Math.Clamp(x + radius + 1, 0, w - 1)];
            }
        }

        // 纵向
        for (int x = 0; x < w; x++)
        {
            int sum = 0;
            for (int y = -radius; y <= radius; y++)
                sum += tmp[Math.Clamp(y, 0, h - 1) * w + x];

            for (int y = 0; y < h; y++)
            {
                src[y * w + x] = (byte)(sum / span);
                sum -= tmp[Math.Clamp(y - radius, 0, h - 1) * w + x];
                sum += tmp[Math.Clamp(y + radius + 1, 0, h - 1) * w + x];
            }
        }
    }
}
