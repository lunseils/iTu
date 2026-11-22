using System.Drawing.Drawing2D;

namespace iTu.Ui;

public enum IconKind
{
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Brush,
    Text,
    Mosaic,
    Blur,
    Number,
    Undo,
    Redo,
    Copy,
    Save,
    Close,
    Ocr,
    Pin,
    Translate,
    Color,
    Width,
    Scissor,
    ScissorSolid,
    Shape,
    Share,
    Check,
    Dropdown,
    Mail,
    Keyboard,
    Video,
    Image,
    Folder,
    Gauge,
    Globe,
    Sparkle,
    Gear,
    Info
}

/// <summary>
/// 胶囊 UI 的图标集 —— 全部矢量绘制，不依赖图片资源。
///
/// 设计规范（与胶囊本体一致）：
///   · 设计栅格 20×20，内容区留 3~4 的安全边，任何尺寸等比缩放；
///   · 描边统一 1.6 单位、圆头圆角，保证一排图标"视觉重量"一致；
///   · 只用单色线条 + 少量实心块，深浅交给调用方的 color；
///   · 不做阴影/渐变，避免小尺寸下发灰、发糊。
/// </summary>
public static class Icons
{
    /// <summary>设计栅格边长，所有坐标按它书写。</summary>
    private const float Grid = 20f;

    /// <summary>统一描边宽度（设计单位）。</summary>
    private const float Stroke = 1.45f;   // 与胶囊图标（复刻版约 1.2px）保持同一视觉重量

    public static void Draw(Graphics g, IconKind kind, RectangleF r, Color color)
    {
        if (r.Width < 1f || r.Height < 1f) return;

        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 等比缩放并居中，图标不会被拉扁
        float scale = Math.Min(r.Width, r.Height) / Grid;
        var state = g.Save();
        g.TranslateTransform(
            r.X + (r.Width - Grid * scale) / 2f,
            r.Y + (r.Height - Grid * scale) / 2f);
        g.ScaleTransform(scale, scale);

        using var pen = new Pen(color, Stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var dot = new SolidBrush(color);

        switch (kind)
        {
            case IconKind.Rectangle:
                using (var p = Round(3.5f, 5f, 13f, 10f, 2f)) g.DrawPath(pen, p);
                break;

            case IconKind.Ellipse:
                g.DrawEllipse(pen, 3.5f, 5f, 13f, 10f);
                break;

            case IconKind.Line:
                g.DrawLine(pen, 4f, 16f, 16f, 4f);
                break;

            case IconKind.Arrow:
                g.DrawLine(pen, 4f, 16f, 15f, 5f);
                g.DrawLine(pen, 15f, 5f, 15f, 10.2f);
                g.DrawLine(pen, 15f, 5f, 9.8f, 5f);
                break;

            case IconKind.Shape:
                using (var p1 = Round(3f, 3f, 9.5f, 9.5f, 2f)) g.DrawPath(pen, p1);
                using (var p2 = Round(7.5f, 7.5f, 9.5f, 9.5f, 2f)) g.DrawPath(pen, p2);
                break;

            case IconKind.Brush:
                // 铅笔：笔身轮廓 + 金属箍
                using (var p = new GraphicsPath())
                {
                    p.AddLine(12.6f, 3.4f, 16.6f, 7.4f);
                    p.AddLine(16.6f, 7.4f, 7.6f, 16.4f);
                    p.AddLine(7.6f, 16.4f, 3.4f, 16.6f);
                    p.AddLine(3.4f, 16.6f, 3.6f, 12.4f);
                    p.CloseFigure();
                    g.DrawPath(pen, p);
                }
                g.DrawLine(pen, 10.8f, 5.2f, 14.8f, 9.2f);
                break;

            case IconKind.Text:
                g.DrawLine(pen, 3.6f, 15.6f, 7.6f, 4.4f);
                g.DrawLine(pen, 7.6f, 4.4f, 11.6f, 15.6f);
                g.DrawLine(pen, 5.3f, 11.4f, 9.9f, 11.4f);
                DrawCaret(g, pen, 16f);
                break;

            case IconKind.Mosaic:
                FillSquare(g, dot, 3.6f, 3.6f, 5.2f);
                FillSquare(g, dot, 11.2f, 11.2f, 5.2f);
                g.DrawRectangle(pen, 11.2f, 3.6f, 5.2f, 5.2f);
                g.DrawRectangle(pen, 3.6f, 11.2f, 5.2f, 5.2f);
                break;

            case IconKind.Blur:
                g.DrawArc(pen, 4f, 4f, 12f, 9.2f, 200f, 260f);
                g.DrawArc(pen, 4f, 1.4f, 12f, 9.2f, 200f, 260f);
                g.DrawArc(pen, 4f, 6.6f, 12f, 9.2f, 200f, 260f);
                break;

            case IconKind.Number:
                g.DrawEllipse(pen, 3.6f, 3.6f, 12.8f, 12.8f);
                g.DrawLine(pen, 9.2f, 7.2f, 10.8f, 6.2f);
                g.DrawLine(pen, 10.8f, 6.2f, 10.8f, 13.8f);
                break;

            case IconKind.Undo:
                // 左向回旋箭头：弧线更大、箭头更明确
                g.DrawArc(pen, 4.2f, 5.6f, 11.6f, 10.4f, 195f, 230f);
                g.DrawLine(pen, 4.4f, 5.2f, 4.2f, 10.2f);
                g.DrawLine(pen, 4.4f, 5.2f, 9.2f, 5.6f);
                break;

            case IconKind.Redo:
                g.DrawArc(pen, 4.2f, 5.6f, 11.6f, 10.4f, 115f, 230f);
                g.DrawLine(pen, 15.6f, 5.2f, 15.8f, 10.2f);
                g.DrawLine(pen, 15.6f, 5.2f, 10.8f, 5.6f);
                break;

            case IconKind.Share:
                // 带竖尾的右箭头（与参考图一致）
                g.DrawLine(pen, 5.6f, 16.4f, 5.6f, 9.6f);
                g.DrawLine(pen, 5.6f, 9.6f, 16.8f, 9.6f);
                g.DrawLine(pen, 13.6f, 6.4f, 16.8f, 9.6f);
                g.DrawLine(pen, 13.6f, 12.8f, 16.8f, 9.6f);
                break;

            case IconKind.Copy:
                using (var p = Round(3.4f, 3.4f, 9.6f, 12f, 2f)) g.DrawPath(pen, p);
                using (var p = Round(7f, 7f, 9.6f, 9.6f, 2f)) g.DrawPath(pen, p);
                break;

            case IconKind.Save:
                g.DrawLine(pen, 10f, 3.6f, 10f, 12.6f);
                g.DrawLine(pen, 6.6f, 9.4f, 10f, 12.8f);
                g.DrawLine(pen, 13.4f, 9.4f, 10f, 12.8f);
                g.DrawLine(pen, 4.4f, 16.4f, 15.6f, 16.4f);
                break;

            case IconKind.Close:
                g.DrawLine(pen, 5.2f, 5.2f, 14.8f, 14.8f);
                g.DrawLine(pen, 14.8f, 5.2f, 5.2f, 14.8f);
                break;

            case IconKind.Check:
                g.DrawLine(pen, 4.4f, 10.4f, 8.2f, 14.2f);
                g.DrawLine(pen, 8.2f, 14.2f, 15.6f, 6f);
                break;

            case IconKind.Ocr:
                g.DrawLine(pen, 7f, 3.8f, 5f, 3.8f);
                g.DrawLine(pen, 5f, 3.8f, 5f, 16.2f);
                g.DrawLine(pen, 5f, 16.2f, 7f, 16.2f);
                g.DrawLine(pen, 13f, 3.8f, 15f, 3.8f);
                g.DrawLine(pen, 15f, 3.8f, 15f, 16.2f);
                g.DrawLine(pen, 15f, 16.2f, 13f, 16.2f);
                g.DrawLine(pen, 8.4f, 13.4f, 10f, 7.8f);
                g.DrawLine(pen, 10f, 7.8f, 11.6f, 13.4f);
                break;

            case IconKind.Pin:
                g.DrawEllipse(pen, 6.8f, 2.8f, 6.4f, 6.4f);
                g.DrawLine(pen, 10f, 9.4f, 10f, 14.6f);
                g.DrawLine(pen, 6.4f, 15f, 13.6f, 15f);
                break;

            case IconKind.Translate:
                // 左侧 A + 右侧双向箭头：一眼是"语言互转"
                g.DrawLine(pen, 3.4f, 13f, 6.4f, 4.4f);
                g.DrawLine(pen, 6.4f, 4.4f, 9.4f, 13f);
                g.DrawLine(pen, 4.6f, 9.8f, 8.2f, 9.8f);
                g.DrawLine(pen, 11.4f, 8.2f, 16.8f, 8.2f);
                g.DrawLine(pen, 15f, 6.2f, 16.8f, 8.2f);
                g.DrawLine(pen, 15f, 10.2f, 16.8f, 8.2f);
                g.DrawLine(pen, 11.4f, 13.2f, 16.8f, 13.2f);
                g.DrawLine(pen, 13.2f, 11.2f, 11.4f, 13.2f);
                g.DrawLine(pen, 13.2f, 15.2f, 11.4f, 13.2f);
                break;

            case IconKind.Keyboard:
                using (var p = Round(2.4f, 6f, 15.2f, 8.6f, 2f)) g.DrawPath(pen, p);
                g.DrawLine(pen, 5f, 9.2f, 5f, 9.2f);
                g.DrawLine(pen, 7.6f, 9.2f, 7.6f, 9.2f);
                g.DrawLine(pen, 10.2f, 9.2f, 10.2f, 9.2f);
                g.DrawLine(pen, 12.8f, 9.2f, 12.8f, 9.2f);
                g.DrawLine(pen, 15f, 9.2f, 15f, 9.2f);
                g.DrawLine(pen, 6.4f, 11.8f, 13.6f, 11.8f);
                break;

            case IconKind.Video:
                using (var p = Round(2.6f, 5.6f, 11.2f, 8.8f, 2f)) g.DrawPath(pen, p);
                using (var p = new GraphicsPath())
                {
                    p.AddLine(14.2f, 8.6f, 17.4f, 6.6f);
                    p.AddLine(17.4f, 6.6f, 17.4f, 13.4f);
                    p.AddLine(17.4f, 13.4f, 14.2f, 11.4f);
                    g.DrawPath(pen, p);
                }
                break;

            case IconKind.Image:
                using (var p = Round(2.6f, 4.4f, 14.8f, 11.2f, 2f)) g.DrawPath(pen, p);
                g.FillEllipse(dot, 5.6f, 7f, 2.6f, 2.6f);
                using (var p = new GraphicsPath())
                {
                    p.AddLine(4.4f, 14.2f, 8.6f, 9.8f);
                    p.AddLine(8.6f, 9.8f, 11.8f, 13.2f);
                    p.AddLine(11.8f, 13.2f, 13.6f, 11.2f);
                    p.AddLine(13.6f, 11.2f, 16.2f, 14.2f);
                    g.DrawPath(pen, p);
                }
                break;

            case IconKind.Folder:
                using (var p = new GraphicsPath())
                {
                    p.AddLine(2.6f, 6.4f, 2.6f, 15.4f);
                    p.AddLine(2.6f, 15.4f, 17.4f, 15.4f);
                    p.AddLine(17.4f, 15.4f, 17.4f, 8.4f);
                    p.AddLine(17.4f, 8.4f, 10f, 8.4f);
                    p.AddLine(10f, 8.4f, 8.4f, 6.4f);
                    p.CloseFigure();
                    g.DrawPath(pen, p);
                }
                break;

            case IconKind.Gauge:
                g.DrawArc(pen, 3.4f, 5.4f, 13.2f, 13.2f, 180f, 180f);
                g.DrawLine(pen, 10f, 12f, 13.6f, 8.4f);
                break;

            case IconKind.Globe:
                g.DrawEllipse(pen, 3.2f, 3.2f, 13.6f, 13.6f);
                g.DrawEllipse(pen, 7.2f, 3.4f, 5.6f, 13.2f);
                g.DrawLine(pen, 3.6f, 10f, 16.4f, 10f);
                break;

            case IconKind.Gear:
                // 齿轮：外圈 + 8 个短齿（齿用粗线，16px 下才分得清是"齿轮"而不是"星芒"）
                using (var ring = new Pen(color, 1.9f))
                    g.DrawEllipse(ring, 5.4f, 5.4f, 9.2f, 9.2f);
                g.DrawEllipse(pen, 8.2f, 8.2f, 3.6f, 3.6f);
                using (var tooth = new Pen(color, 2.2f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                    for (int i = 0; i < 8; i++)
                    {
                        double a = Math.PI / 4 * i + Math.PI / 8;
                        float x1 = 10 + (float)Math.Cos(a) * 6.0f, y1 = 10 + (float)Math.Sin(a) * 6.0f;
                        float x2 = 10 + (float)Math.Cos(a) * 7.8f, y2 = 10 + (float)Math.Sin(a) * 7.8f;
                        g.DrawLine(tooth, x1, y1, x2, y2);
                    }
                break;

            case IconKind.Info:
                g.DrawEllipse(pen, 3.4f, 3.4f, 13.2f, 13.2f);
                g.FillEllipse(dot, 9.1f, 6.2f, 1.8f, 1.8f);
                g.DrawLine(pen, 10f, 9.6f, 10f, 13.8f);
                break;

            case IconKind.Sparkle:
                g.DrawLine(pen, 10f, 3.2f, 10f, 16.8f);
                g.DrawLine(pen, 3.2f, 10f, 16.8f, 10f);
                g.DrawLine(pen, 5.4f, 5.4f, 14.6f, 14.6f);
                g.DrawLine(pen, 14.6f, 5.4f, 5.4f, 14.6f);
                break;

            case IconKind.Mail:
                using (var p = Round(3.2f, 5f, 13.6f, 10f, 2.4f)) g.DrawPath(pen, p);
                g.DrawLine(pen, 4.2f, 6.4f, 10f, 11f);
                g.DrawLine(pen, 15.8f, 6.4f, 10f, 11f);
                break;

            case IconKind.Color:
                using (var p = Round(3.6f, 3.6f, 12.8f, 12.8f, 3f)) g.DrawPath(pen, p);
                g.FillEllipse(dot, 7.6f, 7.6f, 4.8f, 4.8f);
                break;

            case IconKind.Width:
                g.DrawLine(pen, 4f, 7f, 16f, 7f);
                using (var thin = new Pen(color, Stroke * 0.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(thin, 4f, 13f, 16f, 13f);
                break;

            case IconKind.Dropdown:
                DrawCaret(g, pen, 10f);
                break;

            case IconKind.Scissor:
            case IconKind.ScissorSolid:
                {
                    bool solid = kind == IconKind.ScissorSolid;
                    g.DrawLine(pen, 5.6f, 4.2f, 14.2f, 12.6f);
                    g.DrawLine(pen, 14.4f, 4.2f, 5.8f, 12.6f);
                    if (solid)
                    {
                        g.FillEllipse(dot, 3.2f, 12.6f, 4.6f, 4.6f);
                        g.FillEllipse(dot, 12.2f, 12.6f, 4.6f, 4.6f);
                    }
                    else
                    {
                        g.DrawEllipse(pen, 3.2f, 12.6f, 4.6f, 4.6f);
                        g.DrawEllipse(pen, 12.2f, 12.6f, 4.6f, 4.6f);
                    }
                    break;
                }
        }

        g.Restore(state);
        g.SmoothingMode = mode;
    }

    /// <summary>下拉小箭头：与图标同色同笔宽，标出"可展开"的格位。</summary>
    private static void DrawCaret(Graphics g, Pen pen, float cx)
    {
        g.DrawLine(pen, cx - 2.2f, 12.8f, cx, 15f);
        g.DrawLine(pen, cx, 15f, cx + 2.2f, 12.8f);
    }

    private static void FillSquare(Graphics g, Brush brush, float x, float y, float size)
        => g.FillRectangle(brush, x, y, size, size);

    /// <summary>圆角矩形路径。</summary>
    private static GraphicsPath Round(float x, float y, float w, float h, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius, Math.Min(w, h) / 2f) * 2f;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
