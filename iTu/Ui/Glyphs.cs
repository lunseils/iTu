using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace iTu.Ui;

/// <summary>
/// 把矢量图标渲染成小位图，供不支持自绘的宿主控件使用（Label.Image 等）。
/// 缓存按"图标 + 尺寸 + 颜色"做键 —— 设置页一打开就是十几个，不能每次重建。
/// </summary>
public static class Glyphs
{
    private static readonly Dictionary<(IconKind, int, int), Bitmap> Cache = new();

    public static Bitmap Small(IconKind kind, int size = 16, Color? color = null)
    {
        var c = color ?? Color.FromArgb(108, 120, 140);   // 比正文浅、比 TextMuted 深，小图标才看得清
        var key = (kind, size, c.ToArgb());

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;

            // 先按 3 倍画再降采样：小尺寸下细线不会因为像素太少而发虚
            const int ss = 3;
            using var big = new Bitmap(size * ss, size * ss, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(big))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Icons.Draw(g, kind, new RectangleF(0, 0, size * ss, size * ss), c);
            }

            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(big, new Rectangle(0, 0, size, size));
            }

            // 自定义颜色是任意 RGB，键理论无限增长；超过上限只丢弃引用，
            // 不 Dispose —— 调用方可能仍持有这张位图。
            if (Cache.Count >= 256) Cache.Clear();
            Cache[key] = bmp;
            return bmp;
        }
    }

    /// <summary>设置项文字 → 图标。没有匹配的走通用圆点，保证不会缺图。</summary>
    public static IconKind ForSetting(string label) => label switch
    {
        "区域截图" => IconKind.Rectangle,
        "屏幕录制" => IconKind.Video,
        "默认动作" => IconKind.Copy,
        "图片格式" => IconKind.Image,
        "文件名模板" => IconKind.Text,
        "保存目录" => IconKind.Folder,
        "录制格式" => IconKind.Video,
        "帧率" => IconKind.Gauge,
        "翻译引擎" => IconKind.Translate,
        "目标语言" => IconKind.Globe,
        "接口地址" => IconKind.Sparkle,
        "模型" => IconKind.Sparkle,
        _ => IconKind.Keyboard
    };
}
