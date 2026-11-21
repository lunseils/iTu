using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace QqShot.Ui;

/// <summary>
/// 菜单背景"图片"：极淡的竖向渐变 + 左上柔光 + 细斜纹。
///
/// 纯白卡片在浅色桌面上容易和窗口边缘糊在一起；铺一层几乎察觉不到的纹理，
/// 卡片边界就清楚了，观感也更接近胶囊的"漂浮"质感。
/// 纹理按尺寸缓存，菜单每次弹出不会重绘。
/// </summary>
public static class MenuTexture
{
    private static readonly Dictionary<Size, Bitmap> Cache = new();

    private static Bitmap? _tile;

    /// <summary>
    /// 平铺用纹理：只有竖向渐变（横向恒定），因此横向平铺天然无缝。
    /// 用于 ToolStrip.BackgroundImage（它只支持 Tile/Stretch，不能按圆角裁剪）。
    /// </summary>
    public static Bitmap Tile()
    {
        if (_tile != null) return _tile;

        const int n = 64;
        var bmp = new Bitmap(n, n, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;

        using (var brush = new LinearGradientBrush(
            new Rectangle(0, 0, n, n),
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 247, 250, 254),
            LinearGradientMode.Vertical))
            g.FillRectangle(brush, 0, 0, n, n);

        // 细斜纹：64×64 一个循环，斜线间距 8 → 平铺无缝
        using (var pen = new Pen(Color.FromArgb(10, 92, 122, 182), 1f))
            for (int x = -n; x < n; x += 8)
                g.DrawLine(pen, x, n, x + n, 0);

        _tile = bmp;
        return bmp;
    }

    /// <summary>按目标尺寸生成背景图（首次生成后缓存）。</summary>
    public static Bitmap For(Size size)
    {
        size = new Size(Math.Max(8, size.Width), Math.Max(8, size.Height));

        lock (Cache)
        {
            if (Cache.TryGetValue(size, out var hit)) return hit;

            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 1) 竖向渐变：顶部更亮，底部略带冷调
            using (var brush = new LinearGradientBrush(
                new Rectangle(0, 0, size.Width, size.Height),
                Color.FromArgb(255, 255, 255, 255),
                Color.FromArgb(255, 246, 249, 253),
                LinearGradientMode.Vertical))
                g.FillRectangle(brush, 0, 0, size.Width, size.Height);

            // 2) 左上角柔光：让卡片有光源方向
            using (var path = new GraphicsPath())
            {
                int r = Math.Max(size.Width, size.Height);
                path.AddEllipse(-r / 3, -r / 2, r, r);
                using var pg = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb(18, 255, 255, 255),
                    SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) }
                };
                g.FillRectangle(pg, 0, 0, size.Width, size.Height);
            }

            // 3) 细斜纹：极低对比，起到"图片背景"的质感
            using (var pen = new Pen(Color.FromArgb(7, 90, 120, 180), 1f))
                for (int x = -size.Height; x < size.Width; x += 7)
                    g.DrawLine(pen, x, size.Height, x + size.Height, 0);

            Cache[size] = bmp;
            return bmp;
        }
    }
}
