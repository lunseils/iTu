using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QqShot.Core;

public enum AnnotationKind
{
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Pen,
    Text,
    Mosaic,
    Blur,
    Number
}

public abstract class Annotation
{
    public AnnotationKind Kind { get; init; }
    public Color Stroke { get; set; } = Color.FromArgb(240, 118, 114);
    public float StrokeWidth { get; set; } = 2f;
    public bool Selected { get; set; }

    /// <summary>绘制到画布（坐标为原图像素坐标）。</summary>
    public abstract void Draw(Graphics g);

    /// <summary>是否命中该标注（用于选择/移动）。</summary>
    public virtual bool HitTest(Point p) => false;

    /// <summary>
    /// 释放渲染缓存（Font / 中间位图）。不释放 <see cref="MosaicAnnotation.Source"/>
    /// 等外部注入的源图，所有权始终在调用方。
    /// </summary>
    public virtual void ReleaseCachedResources() { }

    public abstract Annotation Clone();

    protected Pen CreatePen() => new(Stroke, StrokeWidth)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round
    };
}

public sealed class RectAnnotation : Annotation
{
    public RectangleF Rect { get; set; }
    public bool Filled { get; set; }

    public override void Draw(Graphics g)
    {
        using var path = Theme2Path(Rect);
        if (Filled)
        {
            using var b = new SolidBrush(Color.FromArgb(60, Stroke));
            g.FillPath(b, path);
        }
        // 描边以路径为中心线展开，视觉宽度与 StrokeWidth 一致
        using var pen = CreatePen();
        g.DrawPath(pen, path);
    }

    public override bool HitTest(Point p) => Rect.Contains(p);

    public override Annotation Clone() => new RectAnnotation
    {
        Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected, Rect = Rect, Filled = Filled
    };

    internal static GraphicsPath Theme2Path(RectangleF r) => Rounded(r, 0);

    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        // 先 clamp 再算直径：否则大半径时 d 仍按原值，
        // 四段弧会互相越过，圆角外溢成"S 形"畸形。
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2f);
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public sealed class EllipseAnnotation : Annotation
{
    public RectangleF Rect { get; set; }
    public bool Filled { get; set; }

    public override void Draw(Graphics g)
    {
        if (Filled)
        {
            using var b = new SolidBrush(Color.FromArgb(60, Stroke));
            g.FillEllipse(b, Rect);
        }
        using var pen = CreatePen();
        g.DrawEllipse(pen, Rect);
    }

    public override bool HitTest(Point p) => Rect.Contains(p);

    public override Annotation Clone() => new EllipseAnnotation
    {
        Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected, Rect = Rect, Filled = Filled
    };
}

public sealed class LineAnnotation : Annotation
{
    public PointF Start { get; set; }
    public PointF End { get; set; }

    public override void Draw(Graphics g)
    {
        using var pen = CreatePen();
        g.DrawLine(pen, Start, End);
    }

    public override bool HitTest(Point p) => DistanceToSegment(p, Start, End) <= Math.Max(6, StrokeWidth * 2);

    public override Annotation Clone() => new LineAnnotation
    {
        Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected, Start = Start, End = End
    };

    internal static float DistanceToSegment(Point p, PointF a, PointF b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0) return (float)Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        float t = (float)(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy));
        t = Math.Clamp(t, 0f, 1f);
        var px = a.X + t * dx;
        var py = a.Y + t * dy;
        return (float)Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
    }
}

public sealed class ArrowAnnotation : Annotation
{
    public PointF Start { get; set; }
    public PointF End { get; set; }

    public override void Draw(Graphics g)
    {
        using var pen = CreatePen();
        g.DrawLine(pen, Start, End);

        var angle = (float)Math.Atan2(End.Y - Start.Y, End.X - Start.X);
        float head = Math.Max(10f, StrokeWidth * 4.5f);
        float spread = 0.42f;

        using var headBrush = new SolidBrush(Stroke);
        var pts = new[]
        {
            End,
            new PointF(End.X - head * (float)Math.Cos(angle - spread), End.Y - head * (float)Math.Sin(angle - spread)),
            new PointF(End.X - head * 0.62f * (float)Math.Cos(angle), End.Y - head * 0.62f * (float)Math.Sin(angle)),
            new PointF(End.X - head * (float)Math.Cos(angle + spread), End.Y - head * (float)Math.Sin(angle + spread))
        };
        g.FillPolygon(headBrush, pts);
    }

    public override bool HitTest(Point p) => LineAnnotation.DistanceToSegment(p, Start, End) <= Math.Max(6, StrokeWidth * 2);

    public override Annotation Clone() => new ArrowAnnotation
    {
        Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected, Start = Start, End = End
    };
}

public sealed class PenAnnotation : Annotation
{
    public List<PointF> Points { get; } = new();

    // 复用点数组，避免每帧 ToArray 分配
    private PointF[] _buffer = Array.Empty<PointF>();

    public override void Draw(Graphics g)
    {
        int n = Points.Count;
        if (n == 0) return;

        if (n == 1)
        {
            // 单点画圆点
            var p = Points[0];
            using var brush = new SolidBrush(Stroke);
            g.FillEllipse(brush, p.X - StrokeWidth / 2, p.Y - StrokeWidth / 2, StrokeWidth, StrokeWidth);
            return;
        }

        if (_buffer.Length < n) _buffer = new PointF[n];
        Points.CopyTo(_buffer, 0);

        using var pen = CreatePen();
        g.DrawLines(pen, _buffer.AsSpan(0, n));
    }

    public override bool HitTest(Point p)
    {
        for (int i = 1; i < Points.Count; i++)
            if (LineAnnotation.DistanceToSegment(p, Points[i - 1], Points[i]) <= Math.Max(6, StrokeWidth * 2))
                return true;
        return false;
    }

    public override Annotation Clone()
    {
        var c = new PenAnnotation { Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected };
        c.Points.AddRange(Points);
        return c;
    }
}

public sealed class TextAnnotation : Annotation
{
    public PointF Position { get; set; }
    public string Text { get; set; } = "";
    public float FontSize { get; set; } = 16f;
    public bool Underline { get; set; }

    // 字体按尺寸缓存，避免每次绘制都构造 GDI+ 字体对象
    private Font? _font;
    private float _fontSizeCached = -1f;

    private Font GetFont()
    {
        if (_font == null || Math.Abs(_fontSizeCached - FontSize) > 0.01f)
        {
            _font?.Dispose();
            _font = new Font("Microsoft YaHei UI", Math.Max(6f, FontSize), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontSizeCached = FontSize;
        }
        return _font;
    }

    public override void Draw(Graphics g)
    {
        if (string.IsNullOrEmpty(Text)) return;

        var font = GetFont();
        using var b = new SolidBrush(Stroke);
        g.DrawString(Text, font, b, Position);

        if (Underline)
        {
            var size = g.MeasureString(Text, font);
            using var pen = new Pen(Stroke, 1.2f);
            g.DrawLine(pen, Position.X, Position.Y + size.Height,
                             Position.X + size.Width, Position.Y + size.Height);
        }
    }

    public override bool HitTest(Point p)
    {
        var size = TextRenderer.MeasureText(Text, GetFont());
        var r = new RectangleF(Position, size);
        r.Inflate(4, 4);
        return r.Contains(p);
    }

    public override void ReleaseCachedResources()
    {
        _font?.Dispose();
        _font = null;
        _fontSizeCached = -1f;
    }

    public override Annotation Clone() => new TextAnnotation
    {
        Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected,
        Position = Position, Text = Text, FontSize = FontSize, Underline = Underline
    };
}

/// <summary>马赛克（像素化）标注：记录区域，绘制时对源图做马赛克处理。</summary>
public sealed class MosaicAnnotation : Annotation
{
    public RectangleF Rect { get; set; }
    public int BlockSize { get; set; } = 12;

    /// <summary>由画布在绘制前注入源图。</summary>
    public Bitmap? Source { get; set; }

    /// <summary>
    /// 源图原点在标注坐标系里的位置。多显示器负坐标布局下，
    /// 整屏快照的 (0,0) 是虚拟桌面左上角而不是屏幕 (0,0)，
    /// 采样区域必须减掉这个偏移。
    /// </summary>
    public Point SourceOrigin { get; set; }

    // 结果缓存：拖动预览时同一区域会被反复重绘，避免每帧重算
    private Bitmap? _cache;
    private Rectangle _cacheRect;
    private int _cacheBlock = -1;

    public override void Draw(Graphics g)
    {
        if (Rect.Width < 1 || Rect.Height < 1) return;
        if (Source == null) return;

        var r = Rectangle.Round(Rect);
        r.Offset(-SourceOrigin.X, -SourceOrigin.Y);
        if (_cache == null || _cacheRect != r || _cacheBlock != BlockSize)
        {
            _cache?.Dispose();
            _cache = RenderMosaic(Source, r, BlockSize);
            _cacheRect = r;
            _cacheBlock = BlockSize;
        }
        g.DrawImage(_cache, Rect);
    }

    internal static Bitmap RenderMosaic(Bitmap source, Rectangle region, int block)
    {
        block = Math.Max(2, block);
        var w = Math.Max(1, region.Width);
        var h = Math.Max(1, region.Height);
        var outBmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(outBmp))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var sw = Math.Min(block, w);
            var sh = Math.Min(block, h);
            using var small = new Bitmap((int)Math.Ceiling(w / (double)sw), (int)Math.Ceiling(h / (double)sh), PixelFormat.Format32bppPArgb);
            using (var sg = Graphics.FromImage(small))
            {
                sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                sg.DrawImage(source, new Rectangle(0, 0, small.Width, small.Height), region, GraphicsUnit.Pixel);
            }
            g.DrawImage(small, new Rectangle(0, 0, w, h));
        }
        return outBmp;
    }

    public override bool HitTest(Point p) => Rect.Contains(p);

    public override void ReleaseCachedResources()
    {
        _cache?.Dispose();
        _cache = null;
        _cacheRect = Rectangle.Empty;
        _cacheBlock = -1;
    }

    public override Annotation Clone() => new MosaicAnnotation
    {
        Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected,
        Rect = Rect, BlockSize = BlockSize, Source = Source, SourceOrigin = SourceOrigin
    };
}

/// <summary>高斯模糊标注。</summary>
public sealed class BlurAnnotation : Annotation
{
    public RectangleF Rect { get; set; }
    public int Radius { get; set; } = 8;

    public Bitmap? Source { get; set; }

    /// <summary>源图原点在标注坐标系里的位置（见 <see cref="MosaicAnnotation.SourceOrigin"/>）。</summary>
    public Point SourceOrigin { get; set; }

    // 结果缓存：模糊是整幅卷积，绝不可以在绘制循环里逐帧重算
    private Bitmap? _cache;
    private Rectangle _cacheRect;
    private int _cacheRadius = -1;

    public override void Draw(Graphics g)
    {
        if (Rect.Width < 2 || Rect.Height < 2) return;
        if (Source == null) return;

        var r = Rectangle.Round(Rect);
        r.Offset(-SourceOrigin.X, -SourceOrigin.Y);
        if (_cache == null || _cacheRect != r || _cacheRadius != Radius)
        {
            _cache?.Dispose();
            _cache = RenderBlur(Source, r, Radius);
            _cacheRect = r;
            _cacheRadius = Radius;
        }
        g.DrawImage(_cache, Rect);
    }

    internal static Bitmap RenderBlur(Bitmap source, Rectangle region, int radius)
    {
        radius = Math.Clamp(radius, 1, 40);
        var w = Math.Max(1, region.Width);
        var h = Math.Max(1, region.Height);
        var outBmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(outBmp))
        {
            g.DrawImage(source, new Rectangle(0, 0, w, h), region, GraphicsUnit.Pixel);
        }
        ApplyBlur(outBmp, radius);
        return outBmp;
    }

    /// <summary>可分离盒式模糊（近似高斯），速度与质量折中。</summary>
    internal static void ApplyBlur(Bitmap bmp, int radius)
    {
        if (radius < 1) return;

        // 用位图自身的像素格式锁定：PArgb 位图按 Argb 锁定会走一次隐式转换，
        // 回写时产生 ±1 色偏，模糊边缘会出现轻微色晕。
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, bmp.PixelFormat);
        try
        {
            int stride = data.Stride;
            var buf = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);

            int width = bmp.Width;
            var rowBuf = new byte[width * 4];
            var outRow = new byte[width * 4];

            // 水平方向：滑动窗口（左右边界做钳位复制）
            for (int y = 0; y < bmp.Height; y++)
            {
                int off = y * stride;
                Array.Copy(buf, off, rowBuf, 0, width * 4);

                for (int c = 0; c < 3; c++)
                {
                    // 初始窗口求和
                    int sum = 0;
                    for (int x = -radius; x <= radius; x++)
                    {
                        int idx = Math.Clamp(x, 0, width - 1);
                        sum += rowBuf[idx * 4 + c];
                    }
                    int windowSize = radius * 2 + 1;

                    for (int x = 0; x < width; x++)
                    {
                        outRow[x * 4 + c] = (byte)(sum / windowSize);

                        int add = Math.Clamp(x + radius + 1, 0, width - 1);
                        int sub = Math.Clamp(x - radius, 0, width - 1);
                        sum += rowBuf[add * 4 + c] - rowBuf[sub * 4 + c];
                    }
                }

                for (int x = 0; x < width; x++)
                {
                    buf[off + x * 4] = outRow[x * 4];
                    buf[off + x * 4 + 1] = outRow[x * 4 + 1];
                    buf[off + x * 4 + 2] = outRow[x * 4 + 2];
                }
            }

            // 垂直方向
            for (int x = 0; x < width; x++)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                int windowSize = radius * 2 + 1;

                for (int y = -radius; y <= radius; y++)
                {
                    int yy = Math.Clamp(y, 0, bmp.Height - 1);
                    int o = yy * stride + x * 4;
                    sumR += buf[o];
                    sumG += buf[o + 1];
                    sumB += buf[o + 2];
                }

                for (int y = 0; y < bmp.Height; y++)
                {
                    int o = y * stride + x * 4;
                    buf[o] = (byte)(sumR / windowSize);
                    buf[o + 1] = (byte)(sumG / windowSize);
                    buf[o + 2] = (byte)(sumB / windowSize);

                    int add = Math.Clamp(y + radius + 1, 0, bmp.Height - 1);
                    int sub = Math.Clamp(y - radius, 0, bmp.Height - 1);
                    int oa = add * stride + x * 4;
                    int os = sub * stride + x * 4;
                    sumR += buf[oa] - buf[os];
                    sumG += buf[oa + 1] - buf[os + 1];
                    sumB += buf[oa + 2] - buf[os + 2];
                }
            }

            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public override bool HitTest(Point p) => Rect.Contains(p);

    public override void ReleaseCachedResources()
    {
        _cache?.Dispose();
        _cache = null;
        _cacheRect = Rectangle.Empty;
        _cacheRadius = -1;
    }

    public override Annotation Clone() => new BlurAnnotation
    {
        Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected,
        Rect = Rect, Radius = Radius, Source = Source, SourceOrigin = SourceOrigin
    };
}

/// <summary>
/// 译文原位替换标注：把原文**擦掉**，在完全相同的位置画上译文。
///
/// 与旧实现的区别：
///   旧版在原文位置盖一层固定的浅色半透明底片再写译文，
///   深色/渐变背景上一眼能看出"贴片"，原文还可能从边缘露出。
///   现在改为：
///     1. 创建标注时由 <see cref="TextBackdrop"/> 从原图采样出
///        逐行左右边缘背景色 + 主背景色 + 文字色；
///     2. 绘制时用采样到的背景色生成一小块"背景还原图"（缓存一次），
///        按行横向渐变填充，整块覆盖行框并外扩几像素以吃掉抗锯齿边缘；
///     3. 译文用采样到的文字色绘制，字号按行高自适应、超宽自动压缩。
///   最终视觉是"原文被抹掉、原位变成译文"，与底图自然融合。
/// </summary>
public sealed class TranslationAnnotation : Annotation
{
    public RectangleF Rect { get; set; }
    public string Text { get; set; } = "";

    /// <summary>是否用采样背景还原（关闭后只画译文，用于调试或特殊场景）。</summary>
    public bool ShowBackdrop { get; set; } = true;

    /// <summary>兜底背景色（无采样数据时使用）。</summary>
    public Color BackgroundColor { get; set; } = Color.White;

    /// <summary>译文颜色（由采样得到的原文字色，保证与画面协调）。</summary>
    public Color TextColor { get; set; } = Color.FromArgb(22, 28, 42);

    /// <summary>行框外扩量（覆盖抗锯齿边缘，同时不侵入相邻行）。</summary>
    public int PadX { get; set; } = 3;
    public int PadY { get; set; } = 2;

    /// <summary>逐行左/右边缘背景色；索引 = y - <see cref="SamplePatch"/>.Top。</summary>
    public Color[]? RowLeft { get; set; }
    public Color[]? RowRight { get; set; }

    /// <summary>采样得到的实际覆盖区域（含行框外的降部/重音墨迹）。</summary>
    public Rectangle SamplePatch { get; set; }

    /// <summary>
    /// 原文字的墨迹高度（像素）。译文按它反推原图字号，做到"和原图一样大"。
    /// 0 表示没量到，退回按行框高度估算。
    /// </summary>
    public int InkHeight { get; set; }

    /// <summary>
    /// 被替换的原文（OCR 结果）。用它自己的墨迹占比反推原图字号：
    /// 同一字号下中文的墨迹比英文高，直接按墨迹高度定译文大小会偏大，
    /// 先用原文校准一次就与字形体系无关了。
    /// </summary>
    public string SourceText { get; set; } = "";

    // ---- 渲染缓存：背景还原图与字号都只算一次 ----
    private Bitmap? _backdropCache;
    private Rectangle _backdropKey;
    private Font? _font;

    /// <summary>实际覆盖区域：优先用采样结果，没有采样时按行框外扩 PadX/PadY。</summary>
    private Rectangle Patch =>
        SamplePatch.Width > 0 && SamplePatch.Height > 0
            ? SamplePatch
            : Rectangle.Round(RectangleF.Inflate(Rect, PadX, PadY));

    public override void Draw(Graphics g)
    {
        if (Rect.Width < 2 || Rect.Height < 2) return;
        if (string.IsNullOrEmpty(Text)) return;

        var patch = Patch;
        if (patch.Width < 1 || patch.Height < 1) return;

        // 铺底范围可能需要比原行框宽一点：替换用的字体（雅黑）通常比原文的
        // 等宽字体宽，硬压到原行框里会把句子截成"…"，看着像丢字。
        var cover = PlanCover(g, patch);
        var font = GetFont(g, Math.Max(2, cover.Width - 2));

        if (ShowBackdrop)
        {
            var backdrop = EnsureBackdrop(cover);
            if (backdrop != null)
                g.DrawImageUnscaled(backdrop, cover.X, cover.Y);
        }

        DrawTranslation(g, cover, font);
    }

    /// <summary>
    /// 决定"要铺多大一块底"：原行框放得下就用原行框；
    /// 放不下就往右扩（最多 2.2 倍宽），仍放不下则保持原行框、交给字号压缩。
    /// </summary>
    private Rectangle PlanCover(Graphics g, Rectangle patch)
    {
        if (_cover.Width > 0 && _coverKey == patch) return _cover;

        var result = patch;
        // 多留 6px：DrawString 的实际排版比 MeasureString(GenericTypographic) 略宽，
        // 正好卡在边界上时会被判成"放不下"而收省略号。
        double need = MeasureWidth(g, Text, TargetSize) + 6;
        if (need > patch.Width)
        {
            int maxW = (int)Math.Ceiling(patch.Width * 2.2f);
            int w = (int)Math.Ceiling(Math.Min(need, maxW));
            result = new Rectangle(patch.X, patch.Y, Math.Max(patch.Width, w), patch.Height);
        }

        _coverKey = patch;
        _cover = result;
        return result;
    }

    /// <summary>
    /// 某段文字在指定像素字号下的排版宽度。
    ///
    /// 必须用**和 DrawString 同一套**格式去量：GenericTypographic 量出来的宽度比
    /// 实际排版窄 1~3%，正好卡边界时会被判成"放不下"，于是明明够宽也被收成"…"。
    /// </summary>
    private static double MeasureWidth(Graphics g, string text, float size)
    {
        using var probe = new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
        return g.MeasureString(text, probe, int.MaxValue, MeasureFormat).Width;
    }

    private static readonly StringFormat MeasureFormat = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.None
    };

    /// <summary>
    /// 生成"背景还原图"：按行在左右边缘背景色之间做横向插值，
    /// 纯色背景得到纯色，渐变背景得到同向渐变，因此看不出补丁边界。
    /// </summary>
    private Bitmap? EnsureBackdrop(Rectangle cover)
    {
        var patch = Patch;                     // 渐变跨度仍按原始补丁算，加宽部分延续右端色
        if (cover.Width < 1 || cover.Height < 1) return null;

        if (_backdropCache != null && _backdropKey == cover) return _backdropCache;

        _backdropCache?.Dispose();
        _backdropCache = null;

        Bitmap bmp;
        try
        {
            // 用非预乘的 32bppArgb：与画布目标格式一致，
            // 避免 PArgb→Argb 的预乘/反预乘往返带来的 ±1 色差（会沿补丁边缘显形）。
            bmp = new Bitmap(cover.Width, cover.Height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, cover.Width, cover.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[cover.Width];

                for (int y = 0; y < cover.Height; y++)
                {
                    var left = ColorAt(RowLeft, y, BackgroundColor);
                    var right = ColorAt(RowRight, y, BackgroundColor);

                    for (int x = 0; x < cover.Width; x++)
                    {
                        // t 超过 1 时取右端色（加宽区域延续它，不会出现色带）
                        float t = patch.Width <= 1 ? 0f : (float)x / (patch.Width - 1);
                        row[x] = Pack(Lerp(left, right, t));
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }

        _backdropCache = bmp;
        _backdropKey = cover;
        return _backdropCache;
    }

    /// <summary>绘制译文：字号按行高自适应，超宽时逐步压缩，仍超宽则省略号收尾。</summary>
    private void DrawTranslation(Graphics g, Rectangle patch, Font font)
    {
        var avail = new RectangleF(patch.X + 1, patch.Y, Math.Max(2, patch.Width - 2), patch.Height);

        using var fmt = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near
        };
        using var brush = new SolidBrush(TextColor);
        g.DrawString(Text, font, brush, avail, fmt);
    }

    /// <summary>
    /// 确定译文字号（结果缓存，避免每次绘制重新测量）。
    ///
    /// 目标是**和原图字号一致**：先用原文字自己的墨迹占比把"墨迹高度"换算回字号
    /// （同一字号下中文比英文占得高，直接用墨迹高度当字号会整体偏大），
    /// 只有实在排不下（铺底已经扩到上限）时才压缩，最后才由 StringFormat 收省略号。
    /// </summary>
    private Font GetFont(Graphics g, float availWidth)
    {
        if (_font != null) return _font;

        float size = TargetSize;
        float minSize = Math.Max(7f, size * 0.62f);

        // 该字号超宽 → 逐步压缩（中译英/英译中长度差很大，必须留这个余量）
        while (size > minSize)
        {
            if (MeasureWidth(g, Text, size) + 3 <= availWidth) break;
            size -= Math.Max(0.5f, size * 0.06f);
        }

        _font = new Font("Microsoft YaHei UI", Math.Max(minSize, size),
            FontStyle.Regular, GraphicsUnit.Pixel);
        Log.Info($"[text] 最终字号 avail={availWidth:F0} 目标={TargetSize:F1} 实排={size:F1} " +
                 $"宽={MeasureWidth(g, Text, Math.Max(minSize, size)):F0} cover={_cover.Width} " +
                 $"patch={Patch.Width} \"{Text}\"");
        return _font;
    }

    /// <summary>目标字号（= 原图字号）：只推算一次。</summary>
    private float TargetSize
    {
        get
        {
            if (_targetSize > 0) return _targetSize;
            _targetSize = EstimateSourceSize();
            return _targetSize;
        }
    }

    private float _targetSize = -1f;
    private Rectangle _cover;
    private Rectangle _coverKey;

    /// <summary>由"原文墨迹高度 + 原文的墨迹占比"反推原图字号（像素）。</summary>
    private float EstimateSourceSize()
    {
        float ink = InkHeight > 1 ? InkHeight : Rect.Height * 0.78f;

        float ratio = InkRatio(SourceText);
        if (ratio <= 0.05f) ratio = 0.72f;          // 没原文（异常路径）：按拉丁字形估

        float size = Math.Clamp(ink / ratio, 8f, 72f);
        Log.Info($"[text] 字号推算 行高={Rect.Height:F0} 墨高={InkHeight} 占比={ratio:F3} " +
                 $"→ {size:F1}px  \"{SourceText}\"");
        return size;
    }

    /// <summary>某段文字在 Microsoft YaHei UI 下的"墨迹高度 / 字号"比值（一次测量，结果缓存）。</summary>
    private static float InkRatio(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0f;
        if (_ratioCache.TryGetValue(text, out var cached)) return cached;

        const float probe = 48f;
        float ratio = 0f;

        try
        {
            using var font = new Font("Microsoft YaHei UI", probe, FontStyle.Regular, GraphicsUnit.Pixel);
            int w = Math.Min(1600, Math.Max(64, text.Length * 56));
            using var bmp = new Bitmap(w, (int)(probe * 2.2f), PixelFormat.Format32bppArgb);
            using (var mg = Graphics.FromImage(bmp))
            {
                mg.Clear(Color.Transparent);
                mg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                mg.DrawString(text, font, Brushes.White, 0, 0, StringFormat.GenericTypographic);
            }

            int top = -1, bottom = -1;
            for (int y = 0; y < bmp.Height; y++)
            {
                bool ink = false;
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    if (bmp.GetPixel(x, y).A > 40) { ink = true; break; }
                }
                if (!ink) continue;
                if (top < 0) top = y;
                bottom = y;
            }

            if (top >= 0) ratio = (bottom - top + 1) / probe;
        }
        catch
        {
            ratio = 0f;
        }

        _ratioCache[text] = ratio;
        return ratio;
    }

    private static readonly Dictionary<string, float> _ratioCache = new();

    private static Color ColorAt(Color[]? colors, int index, Color fallback)
    {
        if (colors == null || colors.Length == 0) return fallback;
        if (index < 0) index = 0;
        if (index >= colors.Length) index = colors.Length - 1;
        return colors[index];
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;
        // 必须四舍五入：直接截断会让每个通道最多低 1，三通道叠起来
        // 足以在补丁边缘形成一条肉眼可见的界线。
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static int Pack(Color c) =>
        unchecked((int)0xFF000000) | (c.R << 16) | (c.G << 8) | c.B;

    public override bool HitTest(Point p) => Rect.Contains(p);

    public override void ReleaseCachedResources()
    {
        _backdropCache?.Dispose();
        _backdropCache = null;
        _backdropKey = Rectangle.Empty;
        _font?.Dispose();
        _font = null;
    }

    public override Annotation Clone() => new TranslationAnnotation
    {
        Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected,
        Rect = Rect, Text = Text, ShowBackdrop = ShowBackdrop,
        BackgroundColor = BackgroundColor, TextColor = TextColor,
        PadX = PadX, PadY = PadY,
        // 出图时走的是这条克隆路径：字号推算要用的"原文 + 墨迹高度"必须一起带过去，
        // 否则预览是原字号、落盘却退回按行高估，两者对不上。
        SourceText = SourceText, InkHeight = InkHeight,
        RowLeft = RowLeft, RowRight = RowRight, SamplePatch = SamplePatch
    };

}

/// <summary>序号笔：在画面上标出 1、2、3… 的圆角序号。</summary>
public sealed class NumberAnnotation : Annotation
{
    public PointF Position { get; set; }
    public int Index { get; set; } = 1;
    public float Radius { get; set; } = 13f;

    private static readonly StringFormat CenterFormat = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center
    };

    private Font? _font;
    private float _fontSizeCached = -1f;

    private Font GetFont()
    {
        var size = Math.Max(6f, Radius * 1.05f);
        if (_font == null || Math.Abs(_fontSizeCached - size) > 0.01f)
        {
            _font?.Dispose();
            _font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            _fontSizeCached = size;
        }
        return _font;
    }

    public override void Draw(Graphics g)
    {
        var rect = new RectangleF(Position.X - Radius, Position.Y - Radius, Radius * 2, Radius * 2);

        using (var brush = new SolidBrush(Stroke))
        using (var path = RectAnnotation.Rounded(rect, Radius))
            g.FillPath(brush, path);

        using var tb = new SolidBrush(Color.White);
        g.DrawString(Index.ToString(), GetFont(), tb, rect, CenterFormat);
    }

    public override bool HitTest(Point p)
    {
        var rect = new RectangleF(Position.X - Radius, Position.Y - Radius, Radius * 2, Radius * 2);
        return rect.Contains(p);
    }

    public override void ReleaseCachedResources()
    {
        _font?.Dispose();
        _font = null;
    }

    public override Annotation Clone() => new NumberAnnotation
    {
        Kind = Kind, Stroke = Stroke, StrokeWidth = StrokeWidth, Selected = Selected,
        Position = Position, Index = Index, Radius = Radius
    };
}
