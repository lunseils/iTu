using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace iTu.Core;

/// <summary>
/// 一行文字所在位置的"底图信息"：用于把原文**擦掉**后原位写译文。
///
/// 为什么要采样：
///   旧实现是在原文位置盖一层固定的浅色半透明底片，再画译文——
///   深色背景或渐变背景上会明显看出"贴片"，原文还可能从边缘露出。
///   这里按行框周边像素估计出
///     · 逐行左右边缘的背景色（可还原横向/纵向渐变），
///     · 背景主色与文字色（用于决定填充色与译文颜色），
///     · 实际覆盖区域（补丁）——包含行框外的降部/重音墨迹，
///   绘制时就能用真实背景色填掉原文，视觉上等于"替换"。
///
/// 取样稳健性（重要）：
///   OCR 行框是紧贴字形的，行框边缘那一列往往落在字形**抗锯齿**上。
///   旧实现每侧只取 2 个像素做算术平均，一个抗锯齿像素就能把整行背景
///   拉成浅灰（实测白底 Δ=26），表现为一条横贯整行的色带。
///   现在每侧取样外扩到 8 像素并取**中位数**，噪声像素被直接滤掉。
///
/// 采样成本：只在行框外扩后的小区域内做一次 LockBits，逐行统计，
/// 单行通常不到 0.3ms，对一次翻译的几十行完全可以接受。
/// </summary>
public sealed class TextBackdrop
{
    /// <summary>采样时的行框（原图坐标，OCR 返回值）。</summary>
    public required Rectangle Box { get; init; }

    /// <summary>实际覆盖区域：行框外扩 + 行框外的实际墨迹（降部/重音）。</summary>
    public required Rectangle Patch { get; init; }

    /// <summary>左右外扩量（像素）。</summary>
    public required int PadX { get; init; }

    /// <summary>上下基础外扩量（像素，实际覆盖以 <see cref="Patch"/> 为准）。</summary>
    public required int PadY { get; init; }

    /// <summary>整行主背景色（用于兜底与整体判断）。</summary>
    public required Color MainBackground { get; init; }

    /// <summary>估计出的原文字色（译文字色优先用它，保证与画面协调）。</summary>
    public required Color TextColor { get; init; }

    /// <summary>逐行左/右边缘背景色，索引 = y - Patch.Top。</summary>
    public required Color[] RowLeft { get; init; }
    public required Color[] RowRight { get; init; }

    /// <summary>
    /// 原文字**实际墨迹**的高度（像素，ascender 顶到 descender 底）。
    ///
    /// 译文要"和原图字号一致"，光看行框高度不够（行框含内部留白，
    /// 中英文的墨迹占比也不同）。这里把行框内每一行都过一遍墨迹判定，
    /// 得到的才是字形真实高度，绘制时用它反推原图字号。
    /// </summary>
    public required int InkHeight { get; init; }

    /// <summary>左右边缘取样宽度：足够宽才能用中位数滤掉抗锯齿像素。</summary>
    private const int EdgeProbe = 8;

    /// <summary>纵向最大外扩（用于覆盖行框外的降部/重音墨迹）。</summary>
    private const int MaxVerticalExtend = 10;

    /// <summary>与当前行背景亮度差超过该值视为墨迹。</summary>
    private const double InkDelta = 40;

    /// <summary>
    /// 采样一行文字的背景与文字色。
    /// </summary>
    /// <param name="source">原图（不含标注）。</param>
    /// <param name="box">OCR 给出的行包围盒（原图坐标）。</param>
    /// <param name="padX">左右外扩像素。</param>
    /// <param name="padY">上下基础外扩像素。</param>
    public static TextBackdrop? Sample(Bitmap source, Rectangle box, int padX, int padY)
    {
        if (source == null) return null;

        var imageBounds = new Rectangle(0, 0, source.Width, source.Height);
        var clamped = Rectangle.Intersect(box, imageBounds);
        if (clamped.Width < 2 || clamped.Height < 2) return null;

        padX = Math.Clamp(padX, 1, 16);
        padY = Math.Clamp(padY, 1, 16);

        // 采样区域：左右各留 EdgeProbe（外侧边缘取样）+ 纵向留 MaxVerticalExtend（找降部墨迹）
        var scan = Rectangle.Intersect(
            Rectangle.Inflate(clamped, EdgeProbe + padX, MaxVerticalExtend + padY),
            imageBounds);
        if (scan.Width <= 0 || scan.Height <= 0) return null;

        int[] pixels;
        try
        {
            pixels = LockRegion(source, scan);
        }
        catch
        {
            return null;
        }

        // ---- 1) 逐行左右边缘色（中位数，滤掉抗锯齿/墨迹噪声）----
        int scanRows = scan.Height;
        var leftByRow = new Color[scanRows];
        var rightByRow = new Color[scanRows];
        var hasLeftByRow = new bool[scanRows];
        var hasRightByRow = new bool[scanRows];

        for (int i = 0; i < scanRows; i++)
        {
            int y = scan.Top + i;
            hasLeftByRow[i] = TryMedianEdge(pixels, scan, y, clamped.Left - EdgeProbe, clamped.Left - 1, out leftByRow[i]);
            hasRightByRow[i] = TryMedianEdge(pixels, scan, y, clamped.Right, clamped.Right + EdgeProbe - 1, out rightByRow[i]);

            // 某一侧超出图像（行框贴边）时用另一侧兜底
            if (!hasLeftByRow[i] && hasRightByRow[i]) { leftByRow[i] = rightByRow[i]; hasLeftByRow[i] = true; }
            if (!hasRightByRow[i] && hasLeftByRow[i]) { rightByRow[i] = leftByRow[i]; hasRightByRow[i] = true; }
        }

        // 主背景 = 行框范围内所有行的左右边缘色平均
        long sumR = 0, sumG = 0, sumB = 0;
        int samples = 0;
        for (int y = clamped.Top; y < clamped.Bottom; y++)
        {
            int i = y - scan.Top;
            if (!hasLeftByRow[i]) continue;
            sumR += leftByRow[i].R + rightByRow[i].R;
            sumG += leftByRow[i].G + rightByRow[i].G;
            sumB += leftByRow[i].B + rightByRow[i].B;
            samples += 2;
        }

        Color main;
        if (samples > 0)
        {
            main = Color.FromArgb(Clamp255((int)(sumR / samples)),
                                  Clamp255((int)(sumG / samples)),
                                  Clamp255((int)(sumB / samples)));
        }
        else
        {
            // 行框横跨整幅图、取不到外侧像素（极端窄图）：退回直方图最高频色
            main = DominantBucketColor(pixels, scan, clamped);
            for (int i = 0; i < scanRows; i++)
            {
                leftByRow[i] = main;
                rightByRow[i] = main;
                hasLeftByRow[i] = true;
                hasRightByRow[i] = true;
            }
        }

        // ---- 2) 找行框外的实际墨迹（降部 g/y/p、重音符号），补丁要盖住它们 ----
        int inkTop = clamped.Top;
        int inkBottom = clamped.Bottom;

        int downLimit = Math.Min(scan.Bottom, clamped.Bottom + MaxVerticalExtend);
        for (int y = clamped.Bottom; y < downLimit; y++)
        {
            if (!RowHasInk(pixels, scan, y, clamped, RowBackdrop(leftByRow, rightByRow, scan, y, main))) break;
            inkBottom = y + 1;
        }

        int upLimit = Math.Max(scan.Top, clamped.Top - MaxVerticalExtend);
        for (int y = clamped.Top - 1; y >= upLimit; y--)
        {
            if (!RowHasInk(pixels, scan, y, clamped, RowBackdrop(leftByRow, rightByRow, scan, y, main))) break;
            inkTop = y;
        }

        // ---- 3) 补丁范围：覆盖墨迹再多留 1 像素，且不小于基础外扩 ----
        int patchTop = Math.Clamp(Math.Min(clamped.Top - padY, inkTop - 1), scan.Top, clamped.Top);
        int patchBottom = Math.Clamp(Math.Max(clamped.Bottom + padY, inkBottom + 1), clamped.Bottom, scan.Bottom);
        int patchLeft = Math.Clamp(clamped.Left - padX, scan.Left, clamped.Left);
        int patchRight = Math.Clamp(clamped.Right + padX, clamped.Right, scan.Right);

        var patch = Rectangle.FromLTRB(patchLeft, patchTop, patchRight, patchBottom);
        if (patch.Width < 1 || patch.Height < 1) return null;

        // ---- 4) 按补丁范围导出逐行边缘色，索引 = y - Patch.Top ----
        var rowLeft = new Color[patch.Height];
        var rowRight = new Color[patch.Height];
        for (int i = 0; i < patch.Height; i++)
        {
            int idx = patch.Top + i - scan.Top;
            if (idx < 0 || idx >= scanRows)
            {
                rowLeft[i] = main;
                rowRight[i] = main;
                continue;
            }
            rowLeft[i] = leftByRow[idx];
            rowRight[i] = rightByRow[idx];
        }

        // ---- 5) 文字色：行框内与背景亮度差最大的高频色 ----
        var textColor = EstimateInkColor(pixels, scan, clamped, main);

        // ---- 6) 墨迹真实高度：行框内逐行判墨迹，取首末墨迹行 ----
        int inkHeight = 0;
        int firstInk = -1, lastInk = -1;
        for (int y = patch.Top; y < patch.Bottom; y++)
        {
            if (!RowHasInk(pixels, scan, y, clamped, RowBackdrop(leftByRow, rightByRow, scan, y, main)))
                continue;
            if (firstInk < 0) firstInk = y;
            lastInk = y;
        }
        if (firstInk >= 0) inkHeight = lastInk - firstInk + 1;

        return new TextBackdrop
        {
            Box = clamped,
            Patch = patch,
            PadX = padX,
            PadY = padY,
            MainBackground = main,
            TextColor = textColor,
            RowLeft = rowLeft,
            RowRight = rowRight,
            InkHeight = inkHeight
        };
    }

    /// <summary>取某一行用于墨迹判定的背景参考色。</summary>
    private static Color RowBackdrop(Color[] left, Color[] right, Rectangle scan, int y, Color fallback)
    {
        int idx = y - scan.Top;
        if (idx < 0 || idx >= left.Length) return fallback;
        var l = left[idx];
        var r = right[idx];
        return Color.FromArgb(
            Clamp255((l.R + r.R) / 2),
            Clamp255((l.G + r.G) / 2),
            Clamp255((l.B + r.B) / 2));
    }

    /// <summary>该行在行框横向范围内是否存在墨迹（与行背景亮度差超过阈值）。</summary>
    private static bool RowHasInk(int[] pixels, Rectangle scan, int y, Rectangle box, Color rowBackdrop)
    {
        if (y < scan.Top || y >= scan.Bottom) return false;

        double bgLuma = Luma(rowBackdrop);
        int rowOffset = (y - scan.Top) * scan.Width;

        for (int x = box.Left; x < box.Right; x++)
        {
            int argb = pixels[rowOffset + (x - scan.Left)];
            if (Math.Abs(Luma(argb) - bgLuma) > InkDelta) return true;
        }
        return false;
    }

    /// <summary>把区域像素一次性拷进托管数组（0xAARRGGBB）。</summary>
    private static int[] LockRegion(Bitmap source, Rectangle region)
    {
        var data = source.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[region.Width * region.Height];
            for (int y = 0; y < region.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride),
                    pixels, y * region.Width, region.Width);
            }
            return pixels;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    /// <summary>直方图最高频色（仅在拿不到行框外侧像素时使用）。</summary>
    private static Color DominantBucketColor(int[] pixels, Rectangle scan, Rectangle box)
    {
        var histogram = new Dictionary<int, int>(256);
        var sums = new Dictionary<int, (long R, long G, long B)>(256);

        Accumulate(pixels, scan, box, histogram, sums);

        int bestKey = -1, bestCount = 0;
        foreach (var pair in histogram)
        {
            if (pair.Value > bestCount) { bestCount = pair.Value; bestKey = pair.Key; }
        }
        return bestKey < 0 || bestCount == 0
            ? Color.White
            : BucketColor(bestKey, bestCount, sums);
    }

    /// <summary>估计文字色：取与背景亮度差最大、且出现次数够多的颜色桶。</summary>
    private static Color EstimateInkColor(int[] pixels, Rectangle scan, Rectangle box, Color background)
    {
        var histogram = new Dictionary<int, int>(256);
        var sums = new Dictionary<int, (long R, long G, long B)>(256);
        Accumulate(pixels, scan, box, histogram, sums);

        double bgLuma = Luma(background);
        int area = Math.Max(1, box.Width * box.Height);
        int minCount = Math.Max(2, area / 60);

        int bestKey = -1;
        int bestCount = 0;
        double bestDelta = 0;

        foreach (var pair in histogram)
        {
            if (pair.Value < minCount) continue;
            var c = BucketColor(pair.Key, pair.Value, sums);
            double delta = Math.Abs(Luma(c) - bgLuma);
            // 先看反差，反差接近时取出现次数更多的（避免把孤立的抗锯齿像素当文字色）
            if (delta > bestDelta + 8 || (Math.Abs(delta - bestDelta) <= 8 && pair.Value > bestCount))
            {
                bestDelta = delta;
                bestKey = pair.Key;
                bestCount = pair.Value;
            }
        }

        if (bestKey >= 0 && bestDelta >= 30)
            return BucketColor(bestKey, bestCount, sums);

        // 对比度不足（例如背景是照片/花纹）→ 按背景亮度取黑或白，保证译文可读
        return bgLuma > 140
            ? Color.FromArgb(24, 28, 36)
            : Color.FromArgb(246, 248, 252);
    }

    /// <summary>统计行框内的量化颜色直方图。</summary>
    private static void Accumulate(int[] pixels, Rectangle scan, Rectangle box,
        Dictionary<int, int> histogram, Dictionary<int, (long R, long G, long B)> sums)
    {
        for (int y = box.Top; y < box.Bottom; y++)
        {
            int rowOffset = (y - scan.Top) * scan.Width - scan.Left;
            for (int x = box.Left; x < box.Right; x++)
            {
                int argb = pixels[rowOffset + x];
                int r = (argb >> 16) & 0xFF;
                int g = (argb >> 8) & 0xFF;
                int b = argb & 0xFF;
                // 5 位量化（每通道 32 档），抗抗锯齿噪声
                int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);

                histogram.TryGetValue(key, out var count);
                histogram[key] = count + 1;

                sums.TryGetValue(key, out var acc);
                sums[key] = (acc.R + r, acc.G + g, acc.B + b);
            }
        }
    }

    private static Color BucketColor(int key, int count, Dictionary<int, (long R, long G, long B)> sums)
    {
        if (count <= 0) return Color.Gray;
        var acc = sums[key];
        return Color.FromArgb(
            Clamp255((int)(acc.R / count)),
            Clamp255((int)(acc.G / count)),
            Clamp255((int)(acc.B / count)));
    }

    /// <summary>
    /// 取指定行上若干个横向相邻像素的**中位数**颜色。
    ///
    /// 为什么用中位数：行框紧贴字形时，最近的一两个像素落在抗锯齿边缘上，
    /// 算术平均会被它们污染（整行偏灰）；中位数对大反差噪声完全免疫。
    /// </summary>
    private static bool TryMedianEdge(int[] pixels, Rectangle scan, int y,
        int x1, int x2, out Color color)
    {
        color = Color.Empty;
        if (y < scan.Top || y >= scan.Bottom) return false;

        int rowOffset = (y - scan.Top) * scan.Width;
        Span<int> r = stackalloc int[EdgeProbe];
        Span<int> g = stackalloc int[EdgeProbe];
        Span<int> b = stackalloc int[EdgeProbe];
        int count = 0;

        for (int x = x1; x <= x2 && count < EdgeProbe; x++)
        {
            if (x < scan.Left || x >= scan.Right) continue;
            int argb = pixels[rowOffset + (x - scan.Left)];
            r[count] = (argb >> 16) & 0xFF;
            g[count] = (argb >> 8) & 0xFF;
            b[count] = argb & 0xFF;
            count++;
        }

        if (count == 0) return false;

        color = Color.FromArgb(Median(r[..count]), Median(g[..count]), Median(b[..count]));
        return true;
    }

    private static int Median(Span<int> values)
    {
        values.Sort();
        int n = values.Length;
        if (n == 0) return 0;
        return (n & 1) == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }

    private static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>感知亮度（0~255）。</summary>
    private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    private static double Luma(int argb) =>
        0.299 * ((argb >> 16) & 0xFF) + 0.587 * ((argb >> 8) & 0xFF) + 0.114 * (argb & 0xFF);
}
