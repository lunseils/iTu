using System.Drawing.Drawing2D;
using QqShot.Core;

namespace QqShot.Ui;

/// <summary>
/// 遮罩层上的标注绘制与创建。
///
/// 这里**直接复用 <see cref="Annotation"/> 类族**（矩形/椭圆/直线/箭头/画笔/文本/序号/马赛克/模糊），
/// 而不是另造一套极简模型 —— 这样填充、粗细、颜色、字号、文字、模糊等
/// 编辑器里有的能力，截图界面天然就有，行为也完全一致。
///
/// 坐标约定：标注存**屏幕坐标**（PointF），绘制时减掉选区左上角得到图片坐标；
/// 最终合成时同理。
/// </summary>
public static class OverlayInk
{
    /// <summary>按工具类型创建一笔新标注（草稿态）。</summary>
    public static Annotation? New(AnnotationKind kind, Color stroke, float width, bool fill, Bitmap? source)
    {
        return kind switch
        {
            AnnotationKind.Rectangle => new RectAnnotation { Kind = kind, Stroke = stroke, StrokeWidth = width, Filled = fill },
            AnnotationKind.Ellipse => new EllipseAnnotation { Kind = kind, Stroke = stroke, StrokeWidth = width, Filled = fill },
            AnnotationKind.Line => new LineAnnotation { Kind = kind, Stroke = stroke, StrokeWidth = width },
            AnnotationKind.Arrow => new ArrowAnnotation { Kind = kind, Stroke = stroke, StrokeWidth = width },
            AnnotationKind.Pen => NewPen(stroke, width),
            AnnotationKind.Mosaic => new MosaicAnnotation { Kind = kind, Stroke = stroke, Source = source },
            AnnotationKind.Blur => new BlurAnnotation { Kind = kind, Stroke = stroke, Source = source },
            AnnotationKind.Number => new NumberAnnotation { Kind = kind, Stroke = stroke, StrokeWidth = width, Index = 1 },
            AnnotationKind.Text => new TextAnnotation { Kind = kind, Stroke = stroke, FontSize = 16f },
            _ => null
        };
    }

    private static PenAnnotation NewPen(Color stroke, float width)
    {
        var p = new PenAnnotation { Kind = AnnotationKind.Pen, Stroke = stroke, StrokeWidth = width };
        return p;
    }

    /// <summary>拖动更新草稿标注（鼠标当前点）。</summary>
    public static void Extend(Annotation draft, PointF start, PointF current)
    {
        switch (draft)
        {
            case RectAnnotation r: r.Rect = Normalize(start, current); break;
            case EllipseAnnotation e: e.Rect = Normalize(start, current); break;
            case MosaicAnnotation m: m.Rect = Normalize(start, current); break;
            case BlurAnnotation b: b.Rect = Normalize(start, current); break;
            case LineAnnotation l: l.Start = start; l.End = current; break;
            case ArrowAnnotation a: a.Start = start; a.End = current; break;
            case PenAnnotation p: p.Points.Add(current); break;
        }
    }

    /// <summary>草稿是否足够有效（太小的拖拽视为误触）。</summary>
    public static bool IsValid(Annotation draft)
    {
        return draft switch
        {
            RectAnnotation r => r.Rect.Width > 2 && r.Rect.Height > 2,
            EllipseAnnotation e => e.Rect.Width > 2 && e.Rect.Height > 2,
            MosaicAnnotation m => m.Rect.Width > 4 && m.Rect.Height > 4,
            BlurAnnotation b => b.Rect.Width > 4 && b.Rect.Height > 4,
            LineAnnotation l => Dist(l.Start, l.End) > 3,
            ArrowAnnotation a => Dist(a.Start, a.End) > 3,
            PenAnnotation p => p.Points.Count > 1,
            _ => true
        };
    }

    /// <summary>把标注绘制到 Graphics（offset = 选区左上角屏幕坐标，标注坐标减它得到图片坐标）。</summary>
    public static void Draw(Graphics g, IEnumerable<Annotation> items, PointF offset)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        foreach (var a in items)
        {
            var state = g.Save();
            g.TranslateTransform(-offset.X, -offset.Y);
            try
            {
                // 需要源图的类型（马赛克/模糊）在保存合成时已有 Source 注入
                a.Draw(g);
            }
            catch (Exception ex)
            {
                // 单条标注绘制失败不影响其余；但必须留日志，
                // 否则预览"少了一条"却完全无从排查。
                Core.Log.Info($"[ink] 预览绘制失败 {a.GetType().Name}: {ex.Message}");
            }
            g.Restore(state);
        }
    }

    /// <summary>把标注合成到最终图片上（图片坐标系）。</summary>
    public static void Composite(Bitmap bmp, IEnumerable<Annotation> items)
    {
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        foreach (var a in items)
        {
            // 马赛克/模糊需要源图：这里已经就是最终图，注入自身。
            // 标注坐标在 Run() 里已经平移到图片坐标，源图原点归零。
            if (a is MosaicAnnotation m) { m.Source = bmp; m.SourceOrigin = Point.Empty; }
            if (a is BlurAnnotation b) { b.Source = bmp; b.SourceOrigin = Point.Empty; }

            // 与预览保持同一策略：单条失败不能让整张图保存失败。
            try
            {
                a.Draw(g);
            }
            catch (Exception ex)
            {
                Core.Log.Info($"[ink] 出图合成失败 {a.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static RectangleF Normalize(PointF a, PointF b) => RectangleF.FromLTRB(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private static float Dist(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
