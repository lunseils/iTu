using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using iTu.Core;

namespace iTu.Ui;

/// <summary>
/// 胶囊下方的二级调节条（形状 / 箭头 / 画笔 / 文字共用）。
///
/// 这是**纯手绘对象**，不是窗口：它把自己画进 <see cref="OverlayForm"/> 的同一张
/// 分层位图，命中也由 <see cref="OverlaySession"/> 统一分发。
/// 尺寸、形状与主胶囊保持一致（同样高度、同样圆角半径）。
/// </summary>
public sealed class ToolFlyout
{
    public sealed record ToolButton(AnnotationKind Kind, IconKind Icon);

    /// <summary>工具的中文名（悬停提示用）。</summary>
    public static string NameOf(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Rectangle => "矩形",
        AnnotationKind.Ellipse => "圆形",
        AnnotationKind.Line => "直线",
        AnnotationKind.Arrow => "箭头",
        AnnotationKind.Pen => "画笔",
        AnnotationKind.Text => "文本",
        AnnotationKind.Number => "序号",
        AnnotationKind.Mosaic => "马赛克",
        AnnotationKind.Blur => "模糊",
        _ => "工具"
    };

    private enum ItemKind { Tool, Fill, Slider, Swatch }

    private readonly List<(ItemKind Kind, Rectangle Rect, int Index)> _hits = new();

    private IReadOnlyList<ToolButton> _tools = Array.Empty<ToolButton>();
    private AnnotationKind _activeTool;
    private bool _showFill;
    private bool _filled;

    private string _sliderLabel = "粗细";
    private int _sliderMin = 1, _sliderMax = 24, _sliderValue = 4;
    private Color _swatch = Theme.Accent;

    private bool _draggingSlider;
    private Rectangle _sliderTrack;      // 只是开始拖动时的快照，供 PointerMove 复用
    private Rectangle _sliderRect;       // 滑杆可视轨道
    private Rectangle _labelRect;        // "粗细 / 大小"
    private Rectangle _valueRect;        // 当前数值

    /// <summary>当前是否正在拖调节条（浮层据此把飞层外的移动继续转发进来）。</summary>
    public bool DraggingSlider => _draggingSlider;

    public event Action<AnnotationKind>? ToolPicked;
    public event Action<bool>? FillToggled;
    public event Action<int>? SliderChanged;
    public event Action? SwatchClicked;

    /// <summary>任何会影响外观的状态变化（请求浮层重绘）。</summary>
    public event Action? Changed;

    private const int PadX = 14;
    private const int Gap = 10;
    private const int Cell = 26;
    private const int SwatchSize = 24;
    private const int LabelW = 34;
    private const int ValueW = 24;
    private const int KnobR = 6;
    private const int SliderMinW = 70;

    /// <summary>次级胶囊在屏幕坐标里的矩形。</summary>
    public Rectangle Bounds { get; private set; }

    /// <summary>
    /// 排下全部元素所需的最小宽度。
    ///
    /// 之前固定取主胶囊宽度的一半（595/2≈297），而"形状"这层的内容
    /// （3 个工具 + 描边/填充 + 标签 + 滑杆 + 数值 + 色块）需要 ~370px，
    /// 结果滑杆被压成 40px、数值直接画在色块上。宽度改由内容决定，
    /// 由调用方在 Place 时取 max(半宽, 本值)。
    /// </summary>
    public int MinimumWidth { get; private set; } = 200;

    /// <summary>当前悬停提示；由浮层画在同一图层。</summary>
    public string? HintText { get; private set; }
    public Point HintPos { get; private set; }
    private int _tipIndex = -1;

    public void Configure(
        IReadOnlyList<ToolButton>? tools,
        AnnotationKind activeTool,
        bool showFill, bool filled,
        string sliderLabel, int min, int max, int value,
        Color swatch)
    {
        _tools = tools ?? Array.Empty<ToolButton>();
        _activeTool = activeTool;
        _showFill = showFill;
        _filled = filled;
        _sliderLabel = sliderLabel;
        _sliderMin = min;
        _sliderMax = Math.Max(min + 1, max);
        _sliderValue = Math.Clamp(value, _sliderMin, _sliderMax);
        _swatch = swatch;
        MinimumWidth = MeasureWidth();
        BuildLayout();
        Changed?.Invoke();
    }

    /// <summary>按内容算出这一层真正需要的宽度（不再拍脑袋取主胶囊一半）。</summary>
    private int MeasureWidth()
    {
        int w = PadX;
        if (_tools.Count > 0) w += _tools.Count * Cell + (_tools.Count - 1) * 4 + Gap;
        if (_showFill) w += 28 + 26 + Gap;
        w += LabelW + 8 + SliderMinW + 10 + ValueW + 12 + SwatchSize + PadX;
        return w;
    }

    /// <summary>颜色弹层选色后立即同步色块，避免手绘缓存停留在旧颜色。</summary>
    public void SetSwatch(Color color)
    {
        if (_swatch.ToArgb() == color.ToArgb()) return;
        _swatch = color;
        Changed?.Invoke();
    }

    /// <summary>设置屏幕位置与尺寸（同层绘制，不再有窗口句柄）。</summary>
    public void Place(Point screenPos, Size size)
    {
        Bounds = new Rectangle(screenPos, size);
        BuildLayout();
        Changed?.Invoke();
        DumpGeometry();
    }

    /// <summary>把布局矩形写进日志，真机脚本据此定位与断言（不做绘制决策）。</summary>
    private void DumpGeometry()
    {
        var parts = new System.Text.StringBuilder();
        foreach (var (kind, rect, index) in _hits)
        {
            if (parts.Length > 0) parts.Append(' ');
            parts.Append($"{kind}#{index}={rect.X},{rect.Y},{rect.Width},{rect.Height}");
        }
        Log.Info($"[flyout] bounds={Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height} " +
                 $"label={_labelRect.X},{_labelRect.Y},{_labelRect.Width},{_labelRect.Height} " +
                 $"value={_valueRect.X},{_valueRect.Y},{_valueRect.Width},{_valueRect.Height} {parts}");
    }

    /// <summary>按当前宽度排版：工具/填充在左，色块贴右，滑杆吃掉中间剩余空间。</summary>
    private void BuildLayout()
    {
        _hits.Clear();
        _sliderRect = _labelRect = _valueRect = Rectangle.Empty;

        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;

        int mid = b.Top + b.Height / 2;
        int x = b.Left + PadX;

        for (int i = 0; i < _tools.Count; i++)
        {
            _hits.Add((ItemKind.Tool, new Rectangle(x, mid - Cell / 2, Cell, Cell), i));
            x += Cell + 4;
        }

        if (_tools.Count > 0) x += Gap;

        if (_showFill)
        {
            _hits.Add((ItemKind.Fill, new Rectangle(x, mid - 13, 26, 26), 0));
            _hits.Add((ItemKind.Fill, new Rectangle(x + 28, mid - 13, 26, 26), 1));
            x += 28 + 26 + Gap;
        }

        // 色块固定最右，数值在它左边，标签在工具右边，滑杆填满中间
        int swatchX = b.Right - PadX - SwatchSize;
        _hits.Add((ItemKind.Swatch, new Rectangle(swatchX, mid - SwatchSize / 2, SwatchSize, SwatchSize), 0));

        _labelRect = new Rectangle(x, mid - 9, LabelW, 18);
        _valueRect = new Rectangle(swatchX - 12 - ValueW, mid - 9, ValueW, 18);

        int sliderX = _labelRect.Right + 8;
        int sliderW = Math.Max(2 * KnobR + 8, _valueRect.Left - 10 - sliderX);
        _sliderRect = new Rectangle(sliderX, mid - 9, sliderW, 18);

        // 命中区比视觉轨道高，手指/鼠标不用精准对在 18px 内
        _hits.Add((ItemKind.Slider, Rectangle.Inflate(_sliderRect, 2, 6), 0));
    }

    /// <summary>滑杆圆头圆心可移动的区间（两端各留一个圆头半径，避免顶出胶囊）。</summary>
    private Rectangle SliderTravel =>
        _sliderRect.IsEmpty ? Rectangle.Empty : Rectangle.Inflate(_sliderRect, -KnobR, 0);

    /// <summary>画到主浮层位图；origin = 浮层客户区左上角的屏幕坐标。</summary>
    public void Draw(Graphics g, PointF origin)
    {
        var b = Bounds;
        if (b.Width <= 8 || b.Height <= 8) return;

        var state = g.Save();
        g.TranslateTransform(-origin.X, -origin.Y);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 与主胶囊同款：半径 = 高度一半 + 柔和投影
        var card = new RectangleF(b.X + 0.5f, b.Y + 0.5f, b.Width - 1f, b.Height - 1f);
        float radius = card.Height / 2f;
        Theme.DrawSoftShadow(g, card, radius, 7, 26);
        Theme.FillRounded(g, card, radius, Theme.Surface, Theme.Border);

        foreach (var (kind, rect, index) in _hits)
        {
            switch (kind)
            {
                case ItemKind.Tool:
                    {
                        bool active = _tools[index].Kind == _activeTool;
                        if (active)
                        {
                            using var path = Theme.RoundedRect(rect, 6);
                            using var brush = new SolidBrush(Theme.AccentSoft);
                            g.FillPath(brush, path);
                        }
                        Icons.Draw(g, _tools[index].Icon,
                            new RectangleF(rect.X + 4, rect.Y + 4, rect.Width - 8, rect.Height - 8),
                            active ? Theme.Accent : Theme.Text);
                        break;
                    }

                case ItemKind.Fill:
                    {
                        bool isFill = index == 1;
                        bool on = isFill == _filled;
                        var box = Rectangle.Inflate(rect, -3, -3);

                        using (var path = Theme.RoundedRect(box, 4))
                        {
                            if (isFill)
                            {
                                using var brush = new SolidBrush(Theme.Fade(Theme.Accent, 0.22f));
                                g.FillPath(brush, path);
                                using var ring = new Pen(Theme.Fade(Theme.Accent, 0.75f), 1.4f);
                                g.DrawPath(ring, path);
                            }
                            else
                            {
                                using var ring = new Pen(Theme.Text, 2.2f);
                                g.DrawPath(ring, path);
                            }
                        }

                        if (on)
                        {
                            using var path = Theme.RoundedRect(Rectangle.Inflate(box, 3, 3), 6);
                            using var sel = new Pen(Theme.Accent, 1.6f);
                            g.DrawPath(sel, path);
                        }
                        break;
                    }

                case ItemKind.Slider:
                    DrawSlider(g, rect);
                    break;

                case ItemKind.Swatch:
                    {
                        var r = Rectangle.Inflate(rect, -1, -1);
                        using var path = Theme.RoundedRect(r, 4);
                        using var brush = new SolidBrush(_swatch);
                        using var pen = new Pen(Theme.BorderStrong, 1.2f);
                        g.FillPath(brush, path);
                        g.DrawPath(pen, path);
                        break;
                    }
            }
        }

        var font = Theme.SmallFont;
        using var muted = new SolidBrush(Theme.TextSecondary);
        using var text = new SolidBrush(Theme.Text);

        // 标签/数值都用排版阶段算好的矩形，不再"按滑杆位置反推"（窄飞层时会压到色块上）
        g.DrawString(_sliderLabel, font, muted, _labelRect, LeftCenter);
        g.DrawString(_sliderValue.ToString(), font, text, _valueRect, RightAlign);

        g.Restore(state);
    }

    private static readonly StringFormat RightAlign = new(StringFormatFlags.NoWrap)
    {
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center
    };

    private static readonly StringFormat LeftCenter = new(StringFormatFlags.NoWrap)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center
    };

    private void DrawSlider(Graphics g, Rectangle track)
    {
        int cy = track.Y + track.Height / 2;

        using (var bg = new Pen(Theme.BorderStrong, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(bg, track.X, cy, track.Right, cy);

        var travel = SliderTravel;
        float t = Math.Clamp((_sliderValue - _sliderMin) / (float)(_sliderMax - _sliderMin), 0f, 1f);
        int knobX = travel.Left + (int)Math.Round(t * Math.Max(1, travel.Width));

        using (var fill = new Pen(Theme.Accent, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(fill, track.X, cy, knobX, cy);

        g.FillEllipse(Brushes.White, knobX - 5.5f, cy - 5.5f, 11f, 11f);
        using var ring = new Pen(Theme.Accent, _draggingSlider ? 2.2f : 1.6f);
        g.DrawEllipse(ring, knobX - 5.5f, cy - 5.5f, 11f, 11f);
    }

    private (ItemKind Kind, Rectangle Rect, int Index)? HitTest(Point screen)
    {
        foreach (var h in _hits)
            if (h.Rect.Contains(screen)) return h;
        return null;
    }

    /// <summary>是否命中次级胶囊（含空白背景）。</summary>
    public bool Contains(Point screen) => Bounds.Contains(screen);

    /// <summary>
    /// 是否点在色块上。
    /// 会话用它区分"再点一次色块＝收起颜色弹层"和"点别的工具＝切换工具"。
    /// </summary>
    public bool IsSwatchAt(Point screen)
    {
        var hit = HitTest(screen);
        return hit != null && hit.Value.Kind == ItemKind.Swatch;
    }

    public void PointerDown(Point screen)
    {
        var hit = HitTest(screen);
        if (hit == null) return;

        switch (hit.Value.Kind)
        {
            case ItemKind.Slider:
                _draggingSlider = true;
                // 只记"圆心可移动区间"：轨道两端各留一个圆头半径，
                // 否则点最右端时值已经是最大，圆头却还顶在胶囊边缘外面。
                _sliderTrack = SliderTravel;
                SetSliderFromX(screen.X, _sliderTrack);
                break;

            case ItemKind.Tool:
                _activeTool = _tools[hit.Value.Index].Kind;
                ToolPicked?.Invoke(_activeTool);
                Changed?.Invoke();
                break;

            case ItemKind.Fill:
                _filled = hit.Value.Index == 1;
                FillToggled?.Invoke(_filled);
                Changed?.Invoke();
                break;

            case ItemKind.Swatch:
                SwatchClicked?.Invoke();
                break;
        }
    }

    public void PointerMove(Point screen)
    {
        if (_draggingSlider)
        {
            // 只用开始拖动时记下的轨道，避免 SetSliderFromX 重建 _hits
            SetSliderFromX(screen.X, _sliderTrack);
            return;
        }

        var hit = HitTest(screen);
        if (hit != null)
        {
            int key = hit.Value.Rect.X * 1000 + (int)hit.Value.Kind;
            if (key != _tipIndex)
            {
                _tipIndex = key;
                var text = hit.Value.Kind switch
                {
                    ItemKind.Tool => NameOf(_tools[hit.Value.Index].Kind),
                    ItemKind.Slider => _sliderLabel,
                    ItemKind.Swatch => "颜色",
                    ItemKind.Fill => hit.Value.Index == 1 ? "填充" : "描边",
                    _ => null
                };
                HintText = text;
                HintPos = new Point(hit.Value.Rect.Left, Bounds.Bottom + 6);
                Changed?.Invoke();
            }
        }
        else if (_tipIndex != -1)
        {
            _tipIndex = -1;
            HintText = null;
            Changed?.Invoke();
        }
    }

    public void PointerUp() => _draggingSlider = false;

    public void PointerLeave()
    {
        _draggingSlider = false;
        _tipIndex = -1;
        HintText = null;
        Changed?.Invoke();
    }

    private void SetSliderFromX(int x, Rectangle track)
    {
        float t = Math.Clamp((x - track.X) / (float)Math.Max(1, track.Width), 0f, 1f);
        int v = _sliderMin + (int)Math.Round(t * (_sliderMax - _sliderMin));
        if (v == _sliderValue) return;

        Log.Info($"[flyout] slider={v} from x={x} track={track.X}..{track.Right}");
        _sliderValue = v;
        SliderChanged?.Invoke(v);
        Changed?.Invoke();
    }

    /// <summary>当前色块颜色（给自定义颜色对话框用）。</summary>
    public Color Swatch => _swatch;
}
