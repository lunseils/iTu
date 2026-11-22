using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using iTu.Core;

namespace iTu.Ui;

/// <summary>
/// 覆盖单个显示器的选区浮层。
///
/// 坐标基准（重要）：
///   所有换算一律基于窗口的**实际客户区几何**（<see cref="PixelOrigin"/> /
///   <see cref="PixelSize"/>，由 ClientToScreen + GetClientRect 实时读取），
///   而不是 Monitor.Bounds。
///   原因：DPI 缩放或多屏下，窗口被 SetWindowPos 定位后的真实位置/尺寸
///   可能与 Monitor.Bounds 存在偏差，用后者换算会导致选区与鼠标错位。
///
/// 性能约定：
///   · 压暗底图按客户区尺寸懒合成一次（并在 <see cref="Prepare"/> 中提前合成，
///     避免把几十毫秒的合帧压到"第一次鼠标事件"上）；
///   · 每帧只用 GDI BitBlt 做 1:1 位块传输，不走 GDI+ 的插值/颜色管理路径；
///   · 只重绘 e.ClipRectangle 覆盖的区域；
///   · 放大镜镜片底纹与网格各自缓存为位图，不逐格填充。
/// </summary>
public sealed class OverlayForm : Form
{
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_SETCURSOR = 0x0020;

    /// <summary>
    /// 整个截图编辑器只有这一个窗口：WS_EX_LAYERED 分层窗口，覆盖整个虚拟桌面。
    /// 所有 UI（遮罩 / 选区 / 控制点 / 扩拉线 / 圆角条 / 胶囊 / 次级胶囊 /
    /// 颜色弹层 / 标注 / 放大镜 / 提示 / 文字输入）都画进 <see cref="_surface"/>
    /// 这一张位图，再由 <see cref="_buffer"/> 一次性提交给 DWM。
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000;   // WS_EX_LAYERED
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
            // 刻意不加 WS_EX_NOACTIVATE：Esc / Enter / 文字输入需要键盘焦点
            return cp;
        }
    }

    /// <summary>整窗内容位图（客户区局部坐标，预乘 alpha）。</summary>
    private Bitmap? _surface;
    private readonly LayeredSurface.SurfaceBuffer _buffer = new();

    /// <summary>已压暗的整屏底图（客户区局部坐标）。</summary>
    private Bitmap? _dimmed;

    /// <summary>放大镜镜片缓存：白底（下层）与网格+十字（上层）。</summary>
    private Bitmap? _lensBack;
    private Bitmap? _lensGrid;

    private Bitmap? _snapshot;

    public OverlayForm(Rectangle virtualBounds)
    {
        VirtualBounds = virtualBounds;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        ShowInTaskbar = false;
        // 分层窗口内容完全由 UpdateLayeredWindow 提供，BackColor 不参与显示。
        // 不要设 Color.Transparent —— 没有 SupportsTransparentBackColor 样式会抛异常。

        // GDI+ 只画内存位图，不再走 WinForms 的 OnPaint/双缓冲路径。
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    public Rectangle VirtualBounds { get; }

    /// <summary>客户区左上角的屏幕坐标（物理像素）。</summary>
    public Point PixelOrigin { get; private set; }

    /// <summary>客户区尺寸（物理像素）。</summary>
    public Size PixelSize { get; private set; } = new(1, 1);

    /// <summary>
    /// 从窗口句柄实时读取真实几何。
    /// 窗口定位（SetWindowPos）之后必须调用一次，否则坐标基准是旧的。
    /// </summary>
    public void SyncWindowMetrics()
    {
        if (!IsHandleCreated) return;

        var pt = new Native.POINT { X = 0, Y = 0 };
        if (Native.ClientToScreen(Handle, ref pt))
            PixelOrigin = new Point(pt.X, pt.Y);

        if (Native.GetClientRect(Handle, out var rc))
            PixelSize = new Size(Math.Max(1, rc.Right - rc.Left), Math.Max(1, rc.Bottom - rc.Top));
    }

    /// <summary>
    /// 预热：同步几何 + 提前合成压暗底图。
    ///
    /// 必须在"开始接收鼠标输入"之前调用。否则首次绘制要现场合成整屏底图
    /// （大屏上十几到几十毫秒），而低级鼠标钩子的回调若超过系统超时阈值，
    /// 该事件会被静默丢弃，表现为"按下后第一段拖动没反应"。
    /// </summary>
    public void Prepare()
    {
        SyncWindowMetrics();
        EnsureSurface();
        EnsureDimmed();
        Render(null);
    }

    /// <summary>
    /// 把脏区画进同一张位图并提交给 DWM。dirtyScreen = null 表示整窗重画。
    /// 分层窗口没有 WinForms 的 OnPaint 路径，所有像素都从这里出去。
    /// </summary>
    public void Render(Rectangle? dirtyScreen)
    {
        if (!IsHandleCreated || PixelSize.Width <= 0 || PixelSize.Height <= 0) return;

        SyncWindowMetrics();
        EnsureSurface();
        EnsureDimmed();

        var full = new Rectangle(0, 0, PixelSize.Width, PixelSize.Height);
        var clip = full;
        if (dirtyScreen.HasValue)
        {
            clip = Rectangle.Intersect(ToLocal(dirtyScreen.Value), full);
            if (clip.Width <= 0 || clip.Height <= 0) return;
        }

        using (var g = Graphics.FromImage(_surface!))
        {
            g.SetClip(clip);
            PaintFrame(g, clip, full);
            g.ResetClip();
        }

        _buffer.Present(Handle, _surface!, PixelOrigin);
    }

    private void EnsureSurface()
    {
        var size = PixelSize;
        if (_surface != null && _surface.Width == size.Width && _surface.Height == size.Height) return;
        _surface?.Dispose();
        _surface = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(_surface)) g.Clear(Color.Transparent);
    }

    /// <summary>桌面快照（虚拟桌面坐标）。</summary>
    public Bitmap? Snapshot
    {
        get => _snapshot;
        set
        {
            if (ReferenceEquals(_snapshot, value)) return;
            _snapshot = value;
            DropDimmed();   // 下一帧按当前客户区尺寸重建
        }
    }

    /// <summary>选区（虚拟桌面物理坐标）。</summary>
    public Rectangle Selection { get; set; } = Rectangle.Empty;

    /// <summary>圆角工具条（左上外侧）的屏幕矩形。</summary>
    public Rectangle RadiusBar { get; set; }

    /// <summary>胶囊工具栏（同一图层绘制）的屏幕矩形。</summary>
    public Rectangle PillScreen { get; set; }

    /// <summary>胶囊上悬停的格位（-1 无）。</summary>
    public int PillHover { get; set; } = -1;

    /// <summary>是否勾选了"阴影"（导出时加柔和投影）。</summary>
    public bool ShadowEnabled { get; set; }

    /// <summary>遮罩层上的标注（屏幕坐标）。</summary>
    public IReadOnlyList<Core.Annotation> Ink { get; set; } = Array.Empty<Core.Annotation>();

    /// <summary>鼠标位置（虚拟桌面物理坐标）。</summary>
    public Point CursorPos { get; set; }

    /// <summary>放大镜取样图（grid × grid）。</summary>
    public Bitmap? MagnifierPixels { get; set; }

    public bool MagnifierVisible { get; set; } = true;

    /// <summary>悬停高亮的窗口矩形（虚拟桌面坐标）。</summary>
    public Rectangle? HoverWindow { get; set; }

    /// <summary>悬停窗口标题（窗口模式显示）。</summary>
    public string? HoverTitle { get; set; }

    /// <summary>吸附目标提示（绘制虚线框）。</summary>
    public Rectangle? SnapHint { get; set; }

    /// <summary>放大镜中心像素颜色，由会话直接给出，避免绘制时回调取色。</summary>
    public Color PixelColor { get; set; } = Color.Black;

    /// <summary>是否正在拖选（决定是否显示光标旁的尺寸标签）。</summary>
    public bool Dragging { get; set; }

    /// <summary>次级胶囊（同层手绘，不再是独立窗口）。</summary>
    public ToolFlyout? Flyout { get; set; }

    /// <summary>颜色弹层（同层手绘，不再是独立窗口）。</summary>
    public ColorPickerChrome? Picker { get; set; }

    /// <summary>顶部居中的功能条（同层手绘，参考图二）。</summary>
    public QuickBar? Bar { get; set; }

    /// <summary>手绘悬停提示（替代独立 HintTip 窗口）。</summary>
    public string? HintText { get; set; }
    public Point HintPos { get; set; }

    /// <summary>
    /// 期望的光标形态。空 = 默认箭头。
    ///
    /// 截图界面上光标本身就是重要反馈：落在脏区里该显示"移动"，
    /// 落在控制点上该显示对应方向的缩放箭头，落进工具胶囊该显示手型。
    /// 分层窗口同样走 WM_SETCURSOR，所以在 WndProc 里直接接管最省事。
    /// </summary>
    public Cursor? DesiredCursor { get; set; }

    /// <summary>屏幕坐标 → 客户区局部坐标。</summary>
    public Rectangle ToLocal(Rectangle virtualRect) => new(
        virtualRect.Left - PixelOrigin.X,
        virtualRect.Top - PixelOrigin.Y,
        virtualRect.Width,
        virtualRect.Height);

    private void DropDimmed()
    {
        _dimmed?.Dispose();
        _dimmed = null;
    }

    /// <summary>按当前客户区尺寸懒合成压暗底图。</summary>
    private void EnsureDimmed()
    {
        var size = PixelSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        if (_dimmed != null && _dimmed.Width == size.Width && _dimmed.Height == size.Height) return;

        DropDimmed();

        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            // 快照 → 底图：1:1 位块传输（不透明源，SRCCOPY 即等价于原来的 SourceCopy 覆盖）
            BlitSnapshot(g, new Rectangle(0, 0, size.Width, size.Height));

            g.CompositingMode = CompositingMode.SourceOver;
            using var mask = new SolidBrush(Theme.MaskFill);
            g.FillRectangle(mask, 0, 0, size.Width, size.Height);
        }
        _dimmed = bmp;
    }

    /// <summary>
    /// 把快照的对应区域按 1:1 拷到客户区局部矩形 dst。
    /// 自动处理越界：只绘制与快照相交的部分。
    ///
    /// 为什么不用 GDI BitBlt：
    ///   实测（2560×1440，Debug）在 WinForms 的双缓冲绘制路径里，
    ///   每帧调用 Graphics.GetHdc() 会强制同步整块后台缓冲（3.7M 像素），
    ///   单次绘制从 1.37ms 劣化到 3.53ms。相比之下 GDI+ 的
    ///   DrawImage(1:1, SourceCopy) 本身就是位块拷贝，已经是更优解，
    ///   因此这里保留 GDI+ 路径，改用"精确脏区"缩小绘制面积来提速。
    /// </summary>
    private void BlitSnapshot(Graphics g, Rectangle dst)
    {
        if (_snapshot == null) return;
        if (dst.Width <= 0 || dst.Height <= 0) return;

        // 局部坐标 → 屏幕坐标 → 快照坐标（虚拟桌面归一化）
        var srcFull = new Rectangle(
            PixelOrigin.X - VirtualBounds.Left + dst.X,
            PixelOrigin.Y - VirtualBounds.Top + dst.Y,
            dst.Width, dst.Height);

        var srcClipped = Rectangle.Intersect(srcFull, new Rectangle(0, 0, _snapshot.Width, _snapshot.Height));
        if (srcClipped.Width <= 0 || srcClipped.Height <= 0) return;

        var dstClipped = new Rectangle(
            dst.X + (srcClipped.X - srcFull.X),
            dst.Y + (srcClipped.Y - srcFull.Y),
            srcClipped.Width, srcClipped.Height);

        g.DrawImage(_snapshot, dstClipped, srcClipped, GraphicsUnit.Pixel);
    }

    /// <summary>把压暗底图的脏区搬到窗口上（1:1 拷贝）。</summary>
    private void BlitDimmed(Graphics g, Rectangle clip)
    {
        var dimmed = _dimmed;
        if (dimmed == null) return;
        g.DrawImage(dimmed, clip, clip, GraphicsUnit.Pixel);
    }

    /// <summary>底图层：压暗底图 + 选区内的清晰画面（都是 1:1 拷贝）。</summary>
    private void PaintBaseLayer(Graphics g, Rectangle r)
    {
        if (_dimmed != null)
        {
            BlitDimmed(g, r);
        }
        else
        {
            var mode = g.CompositingMode;
            g.CompositingMode = CompositingMode.SourceOver;
            using var b = new SolidBrush(Color.FromArgb(32, 34, 40));
            g.FillRectangle(b, r);
            g.CompositingMode = mode;
        }

        var sel = ToLocal(Selection);
        if (sel.Width > 0 && sel.Height > 0)
        {
            var dirty = Rectangle.Intersect(r, sel);
            if (dirty.Width > 0 && dirty.Height > 0)
                BlitSnapshot(g, dirty);
        }
    }

    // 分层窗口的内容由 Render() 提交；WinForms 的 OnPaint 不再参与绘制。
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>所有 UI 画进同一张位图（这里只负责画，不负责提交）。</summary>
    private void PaintFrame(Graphics g, Rectangle clip, Rectangle full)
    {
        g.PageUnit = GraphicsUnit.Pixel;
        // 1:1 拷贝用最近邻 + 半像素偏移：与位块传输等价，且不做插值采样
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;

        // 1) 底图 + 选区：单次 1:1 拷贝，且限于脏区
        PaintBaseLayer(g, clip);

        g.CompositingMode = CompositingMode.SourceOver;

        var sel = ToLocal(Selection);

        // 2) 吸附提示
        if (SnapHint.HasValue)
        {
            var hint = ToLocal(SnapHint.Value);
            if (hint.IntersectsWith(clip))
            {
                using var pen = new Pen(Color.FromArgb(200, 94, 143, 246), 1f) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(pen, hint.X, hint.Y, hint.Width - 1, hint.Height - 1);
            }
        }

        // 3) 悬停窗口高亮 + 标题（"自动识别窗口"的主反馈）
        if (HoverWindow.HasValue)
        {
            var lw = ToLocal(HoverWindow.Value);
            if (lw.IntersectsWith(clip))
            {
                // 先把这一窗的画面恢复成原始亮度：用户看到的应该正是"点下去会截到的那块"，
                // 而不是压暗版。只压一层极淡主色 + 2px 描边，作为"已识别"的暗示。
                var clean = Rectangle.Intersect(clip, lw);
                if (clean.Width > 0 && clean.Height > 0) BlitSnapshot(g, clean);
                DrawHoverWindow(g, lw);
            }
        }

        // 4) 选区边框（在选区之上，保证清晰可见）
        if (sel.Width > 0 && sel.Height > 0)
            DrawSelectionFrame(g, sel);

        // 4.4) 在遮罩层上直接画标注（不跳转编辑器）
        if (Ink.Count > 0)
        {
            var state = g.Save();
            g.SetClip(sel);
            // 标注存屏幕坐标，Graphics 是浮层客户区坐标。
            // 偏移必须是窗口真实原点 PixelOrigin（不是选区左上角），
            // 否则选区不在屏幕原点时整批标注会整体偏移，出现"预览与出图不一致"。
            OverlayInk.Draw(g, Ink, new PointF(PixelOrigin.X, PixelOrigin.Y));
            g.Restore(state);
        }

        // 4.5) 胶囊工具栏：与圆角胶囊、扩拉线同处**这一个图层**，
        //      不再是独立窗口 —— 点它的"保存"才是真正截图
        if (PillScreen.Width > 8 && clip.IntersectsWith(ToLocal(PillScreen)))
        {
            var local = ToLocal(PillScreen);
            var state = g.Save();
            g.TranslateTransform(local.X, local.Y);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PillChrome.Draw(g, new Rectangle(0, 0, local.Width, local.Height), PillHover);
            g.Restore(state);
        }


        // 4.6) 次级胶囊 + 颜色弹层：与胶囊共用同一张位图，不再有独立窗口
        var origin = new PointF(PixelOrigin.X, PixelOrigin.Y);
        if (Flyout != null && clip.IntersectsWith(ToLocal(Flyout.Bounds)))
            Flyout.Draw(g, origin);
        if (Picker != null && clip.IntersectsWith(ToLocal(Picker.Bounds)))
            Picker.Draw(g, origin);

        // 4.7) 顶部功能条（截图/录屏/长截图/隐藏当前窗口），框选时由会话隐藏
        if (Bar is { Visible: true } bar)
        {
            var barLocal = ToLocal(bar.Bounds);
            var barClip = new Rectangle(barLocal.X - 12, barLocal.Y - 12, barLocal.Width + 24, barLocal.Height + 56);
            if (clip.IntersectsWith(barClip))
                bar.Draw(g, origin);
        }

        // 5) 放大镜
        if (MagnifierVisible && MagnifierPixels != null)
            DrawMagnifier(g, clip);

        // 6) 手绘悬停提示（与 HintTip 独立窗口视觉一致）
        //    HintPos 是屏幕坐标，这里必须换成浮层客户区坐标（多屏/负坐标时否则整体偏移）
        if (!string.IsNullOrEmpty(HintText))
            DrawHint(g, HintText!, ToLocal(new Rectangle(HintPos, Size.Empty)).Location);

        // 7) 拖选时的实时分辨率（最后画，保证始终可见）
        if (Dragging && sel.Width > 0 && sel.Height > 0)
            DrawSizeBadge(g, sel.Size);
    }

    /// <summary>同层手绘提示条：替代截图态的 HintTip 窗口。</summary>
    private static void DrawHint(Graphics g, string text, Point localPos)
    {
        // Theme.SmallFont 是全局共享实例，绝不能 using（释放后所有后续绘制都会抛
        // "Parameter is not valid"，表现为截图界面出过一次提示就整体不再刷新）。
        var font = Theme.SmallFont;
        var size = g.MeasureString(text, font);
        var box = new RectangleF(localPos.X, localPos.Y, size.Width + 16, size.Height + 8);

        using var path = Theme.RoundedRect(box, 6);
        using (var shadow = new SolidBrush(Color.FromArgb(60, 8, 12, 18)))
        using (var sp = Theme.RoundedRect(
                   new RectangleF(box.X + 1, box.Y + 2, box.Width, box.Height), 6))
            g.FillPath(shadow, sp);
        using (var fill = new SolidBrush(Color.FromArgb(244, 250, 252, 255)))
            g.FillPath(fill, path);
        using (var pen = new Pen(Theme.Border, 1f))
            g.DrawPath(pen, path);
        using var brush = new SolidBrush(Theme.Text);
        g.DrawString(text, font, brush, box.X + 8, box.Y + 4);
    }

    /// <summary>尺寸标签的固定尺寸（"1234 × 5678" 足够放下）。</summary>
    private static readonly Size SizeBadgeSize = new(96, 26);

    /// <summary>
    /// 尺寸标签位置。放大镜默认占据光标右下（+22），所以标签默认放**左下方**，
    /// 放大镜因贴边翻到左边时标签同步翻到右边，两者始终不重叠。
    /// </summary>
    public static Rectangle GetSizeBadgeBounds(Point cursorLocal, Size viewSize)
    {
        const int Gap = 18;
        int w = SizeBadgeSize.Width;
        int h = SizeBadgeSize.Height;

        // 与放大镜的水平落位保持一致的反向选择
        bool lensOnRight = cursorLocal.X + 22 + LensSize + 24 <= viewSize.Width;

        int x = lensOnRight ? cursorLocal.X - Gap - w : cursorLocal.X + Gap;
        int y = cursorLocal.Y + Gap;

        x = Math.Max(2, Math.Min(x, Math.Max(2, viewSize.Width - w - 2)));
        y = Math.Max(2, Math.Min(y, Math.Max(2, viewSize.Height - h - 2)));

        return new Rectangle(x, y, w, h);
    }

    private void DrawSizeBadge(Graphics g, Size size)
    {
        var local = new Point(CursorPos.X - PixelOrigin.X, CursorPos.Y - PixelOrigin.Y);
        var box = GetSizeBadgeBounds(local, PixelSize);

        using var path = Theme.RoundedRect(
            new RectangleF(box.X, box.Y, box.Width, box.Height), 7);

        using (var shadow = new SolidBrush(Color.FromArgb(70, 8, 12, 18)))
        {
            using var sp = Theme.RoundedRect(
                new RectangleF(box.X + 1, box.Y + 2, box.Width, box.Height), 7);
            g.FillPath(shadow, sp);
        }

        using (var fill = new SolidBrush(Color.FromArgb(235, 26, 30, 38)))
            g.FillPath(fill, path);

        var text = $"{size.Width} × {size.Height}";
        using var fmt = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var brush = new SolidBrush(Color.White);
        g.DrawString(text, Theme.UiFontBold, brush, box, fmt);
    }

    /// <summary>
    /// 悬停窗口高亮：极淡的主色填充 + 2px 描边 + 左上角标题牌。
    ///
    /// 只描边时在"窗口本身就是浅色"的场合几乎看不见；加一层 18/255 的填充
    /// 既不遮挡内容（用户还要看里面的字），又能一眼看出"这一整块会被截下来"。
    /// </summary>
    private void DrawHoverWindow(Graphics g, Rectangle lw)
    {
        var r = new RectangleF(lw.X + 1f, lw.Y + 1f, lw.Width - 2f, lw.Height - 2f);

        using (var fill = new SolidBrush(Color.FromArgb(20, 94, 143, 246)))
            g.FillRectangle(fill, r);

        using (var pen = new Pen(Color.FromArgb(220, 94, 143, 246), 2f))
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);

        if (!string.IsNullOrEmpty(HoverTitle))
            DrawHoverTitle(g, lw);
    }

    private void DrawHoverTitle(Graphics g, Rectangle lw)
    {
        var font = Theme.UiFontBold;

        // 标题 + 程序名 + 尺寸可能很长：超宽就按字符截断加省略号，
        // 否则标签会横穿整个屏幕，比窗口本身还显眼。
        const float MaxW = 560f;
        var title = HoverTitle!;
        if (g.MeasureString(title, font).Width > MaxW)
        {
            while (title.Length > 4 && g.MeasureString(title + "…", font).Width > MaxW)
                title = title[..^1];
            title += "…";
        }

        var size = g.MeasureString(title, font);
        float boxW = Math.Min(size.Width + 20, ClientRectangle.Width - 4f);
        float x = Math.Clamp(lw.X, 2, Math.Max(2f, ClientRectangle.Width - boxW - 2));
        var box = new RectangleF(x, Math.Max(2, lw.Y - size.Height - 12), boxW, size.Height + 8);

        using (var shadowPath = Theme.RoundedRect(new RectangleF(box.X + 1, box.Y + 2, box.Width, box.Height), 8))
        using (var sb = new SolidBrush(Color.FromArgb(80, 10, 14, 20)))
            g.FillPath(sb, shadowPath);

        using (var titlePath = Theme.RoundedRect(box, 8))
        {
            using (var sb = new SolidBrush(Color.FromArgb(240, 250, 252, 255)))
                g.FillPath(sb, titlePath);
            using (var pen2 = new Pen(Color.FromArgb(90, 94, 143, 246), 1f))
                g.DrawPath(pen2, titlePath);
        }

        using var tb = new SolidBrush(Theme.Text);
        g.DrawString(title, font, tb, box.X + 10, box.Y + 4);
    }

    /// <summary>圆角半径（屏幕像素），由会话同步过来，用于实时预览。</summary>
    public int Radius { get; set; }

    /// <summary>选区在屏幕坐标里的位置（用于画工具条）。</summary>
    public Rectangle SelectionScreen { get; set; }

    private bool _radiusMaskFill = true;

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(1f, Math.Min(radius, Math.Min(r.Width, r.Height) / 2f) * 2f);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 选区框 + 8 个控制点 + 四边扩拉线。
    ///
    /// 尺寸照参考图实测（1:1 像素）：
    ///   · 边框 2px（旧版 1 / 1.4px，在高分屏上细得像一根发丝）；
    ///   · 控制点白底圆、直径 12px、描边 1.6px（旧版 9×1.5，偏小）；
    ///   · 扩拉线 18px 长、1.2px 粗、白色圆头 —— 参考图里只在边缘外侧露一小截。
    /// </summary>
    private void DrawSelectionFrame(Graphics g, Rectangle sel)
    {
        var penColor = Color.FromArgb(94, 143, 246);
        const float frameWidth = 2f;

        // 圆角 > 0 时：按圆角画边框，并把四角区域压暗 —— 保存后这些角是透明的
        if (Radius > 0)
        {
            float r = Math.Min(Radius, Math.Min(sel.Width, sel.Height) / 2f);
            using var path = RoundedRect(sel, r);

            if (_radiusMaskFill)
            {
                using var outside = new Region(sel);
                outside.Exclude(path);
                using var mask = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                g.FillRegion(mask, outside);
            }

            using (var pen = new Pen(penColor, frameWidth))
                g.DrawPath(pen, path);
        }
        else
        {
            using var pen = new Pen(penColor, frameWidth);
            g.DrawRectangle(pen, sel.X + 1, sel.Y + 1, sel.Width - 2, sel.Height - 2);
        }

        // 8 个把手：四角 + 四边中点统一用小圆圈（照参考图 1:1 量的 8px、1.1px 环）
        const float dot = 8f;
        using var dotFill = new SolidBrush(Color.White);
        using var dotPen = new Pen(penColor, 1.1f);

        foreach (var pt in OverlaySession.GripPoints(sel))
        {
            var box = new RectangleF(pt.X - dot / 2f, pt.Y - dot / 2f, dot, dot);
            g.FillEllipse(dotFill, box);
            g.DrawEllipse(dotPen, box);
        }

        // 中间控制点两侧的"扩拉线"已按需求取消：那两根白色短线看着像控制点长了毛，
        // 需要拉边时直接拖控制点即可（命中半径 9px，比线还好点）。

        DrawToolbar(g);
    }

    private void DrawToolbar(Graphics g)
    {
        if (RadiusBar.Width <= 8) return;

        var barScreen = RadiusBar;
        var bar = ToLocal(barScreen);
        if (!ClientRectangle.IntersectsWith(bar)) return;

        // 胶囊背景：与主胶囊同一套 —— 柔影 + 白底 + 细描边 + 极淡纹理
        float radius = bar.Height / 2f;
        var shadowRect = new RectangleF(bar.X, bar.Y + 2, bar.Width, bar.Height);
        for (int i = 3; i >= 1; i--)
        {
            using var shadow = new SolidBrush(Color.FromArgb(14, 0, 0, 0));
            using var sp = RoundedRect(RectangleF.Inflate(shadowRect, i, i), radius + i);
            g.FillPath(shadow, sp);
        }

        using (var path = RoundedRect(bar, radius))
        {
            // 参考图里这条就是纯白平涂（没有主胶囊那层纹理），照做
            using var fill = new SolidBrush(Color.FromArgb(252, 255, 255, 255));
            g.FillPath(fill, path);

            using var pen = new Pen(Color.FromArgb(60, 120, 140, 170), 1f);
            g.DrawPath(pen, path);
        }

        // 参考图的字是近黑、灰度抗锯齿；ClearType 会在放大后露出彩色描边，和参考不一致
        using var text = new SolidBrush(Color.FromArgb(10, 12, 16));
        using var muted = new SolidBrush(Color.FromArgb(10, 12, 16));
        var hintBackup = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var (sliderS, shadowS, _) = OverlaySession.ToolbarParts(barScreen);
        var slider = ToLocal(sliderS);
        var shadowBox = ToLocal(shadowS);

        // 字体比原来大一档（8pt → 9.25pt）：34px 高的条里 8pt 字上下都是空的，看着"细"
        const float lineH = 20f;
        float textY = bar.Top + (bar.Height - lineH) / 2f;

        // 尺寸
        g.DrawString($"{SelectionScreen.Width} × {SelectionScreen.Height}", Theme.ToolbarFont, text,
            bar.Left + 18, textY);

        // 圆角标签
        g.DrawString("圆角", Theme.ToolbarFont, muted, bar.Left + 116, textY);

        // 滑杆（轨道中心线在 slider 中线）
        float trackY = slider.Top + slider.Height / 2f;
        using (var track = new Pen(Color.FromArgb(205, 212, 222), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, slider.Left, trackY, slider.Right, trackY);

        int limit = Math.Min(Math.Min(SelectionScreen.Width, SelectionScreen.Height) / 2, 400);
        float t = limit <= 0 ? 0 : Math.Clamp(Radius / (float)limit, 0f, 1f);
        float kx = slider.Left + t * slider.Width;

        using (var fill = new Pen(Theme.Accent, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(fill, slider.Left, trackY, kx, trackY);

        // 圆钮：白底 + 细环。没调过圆角时环是淡灰的（不抢视线），
        // 一旦有值才变主色，和"滑杆已生效"的语义一致。
        var ringColor = Radius > 0 ? Theme.Accent : Color.FromArgb(214, 220, 229);
        g.FillEllipse(Brushes.White, kx - 7f, trackY - 7f, 14, 14);
        using (var ring = new Pen(ringColor, 1.5f))
            g.DrawEllipse(ring, kx - 7f, trackY - 7f, 14, 14);

        // 数值（紧跟在滑杆右侧）
        g.DrawString(Radius.ToString(), Theme.ToolbarFont, muted, bar.Left + 276, textY);

        // 阴影 + 勾选框（15px 圆角方框、浅灰描边、勾选态填主色 + 白勾）
        using (var labelFmt = new StringFormat
               {
                   Alignment = StringAlignment.Far,
                   LineAlignment = StringAlignment.Center,
                   FormatFlags = StringFormatFlags.NoWrap
               })
        {
            // 文字右端距勾选框 10px，左侧留够给数值
            var labelRect = new RectangleF(bar.Left + 276, bar.Top,
                Math.Max(24, shadowBox.Left - 10 - (bar.Left + 276)), bar.Height);
            g.DrawString("阴影", Theme.ToolbarFont, muted, labelRect, labelFmt);
        }

        var cb = new RectangleF(shadowBox.X + 0.5f, shadowBox.Y + 0.5f, shadowBox.Width - 1, shadowBox.Height - 1);
        using (var cbPath = RoundedRect(cb, 4f))
        {
            if (ShadowEnabled)
            {
                using var fill = new SolidBrush(Theme.Accent);
                g.FillPath(fill, cbPath);
            }
            else
            {
                using var fill = new SolidBrush(Color.FromArgb(252, 253, 255));
                g.FillPath(fill, cbPath);
                using var edge = new Pen(Color.FromArgb(198, 205, 216), 1.2f);
                g.DrawPath(edge, cbPath);
            }
        }

        if (ShadowEnabled)
        {
            using var tick = new Pen(Color.White, 1.9f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            float cx = cb.Left, cy = cb.Top;
            g.DrawLines(tick, new[]
            {
                new PointF(cx + 3.6f, cy + 8.0f),
                new PointF(cx + 6.4f, cy + 10.8f),
                new PointF(cx + 11.6f, cy + 4.6f)
            });
        }

        // 还原绘制提示：这张位图后面还要画标注/放大镜，不能带着这里的设置跑
        g.TextRenderingHint = hintBackup;
    }

    private const int LensCell = 14;   // 单像素放大后的边长
    private const int LensGrid = 11;   // 取样网格数
    private const int LensSize = LensCell * LensGrid;

    /// <summary>
    /// 计算放大镜占据的屏幕局部区域（含读数条），供绘制与脏区计算共用。
    /// </summary>
    public static Rectangle GetMagnifierBounds(Point cursorLocal, Size viewSize)
    {
        int mx = cursorLocal.X + 22;
        int my = cursorLocal.Y + 22;
        if (mx + LensSize + 24 > viewSize.Width) mx = cursorLocal.X - LensSize - 22;
        if (my + LensSize + 40 > viewSize.Height) my = cursorLocal.Y - LensSize - 40;
        mx = Math.Max(4, mx);
        my = Math.Max(4, my);
        return new Rectangle(mx - 4, my - 4, LensSize + 8, LensSize + 44);
    }

    private void DrawMagnifier(Graphics g, Rectangle clip)
    {
        var mag = MagnifierPixels;
        if (mag == null) return;

        var cursorLocal = new Point(CursorPos.X - PixelOrigin.X, CursorPos.Y - PixelOrigin.Y);

        var totalRect = GetMagnifierBounds(cursorLocal, PixelSize);
        if (!totalRect.IntersectsWith(clip)) return;

        var lensRect = new Rectangle(totalRect.X + 4, totalRect.Y + 4, LensSize, LensSize);

        EnsureLensChrome();

        // 分层绘制：白底 → 放大像素 → 网格与十字（必须在像素之上，否则会被盖住）
        if (_lensBack != null)
            g.DrawImageUnscaled(_lensBack, lensRect.X, lensRect.Y);

        g.DrawImage(mag, lensRect,
            new Rectangle(0, 0, mag.Width, mag.Height), GraphicsUnit.Pixel);

        if (_lensGrid != null)
            g.DrawImageUnscaled(_lensGrid, lensRect.X, lensRect.Y);

        // 镜片外框
        using (var border = new Pen(Color.FromArgb(90, 0, 0, 0), 1f))
            g.DrawRectangle(border, lensRect.X, lensRect.Y, lensRect.Width - 1, lensRect.Height - 1);

        // 颜色读数
        var c = PixelColor;
        var info = $"#{c.R:X2}{c.G:X2}{c.B:X2}  R{c.R} G{c.G} B{c.B}";
        using (var bg = new SolidBrush(Color.FromArgb(238, 250, 252, 255)))
            g.FillRectangle(bg, lensRect.X, lensRect.Bottom + 3, lensRect.Width, 20);
        using (var tb = new SolidBrush(Color.FromArgb(60, 70, 86)))
            g.DrawString(info, Theme.MonoFont, tb, lensRect.X + 6, lensRect.Bottom + 6);
    }

    /// <summary>
    /// 构建镜片缓存：白色底 + 网格/十字前景层。只做一次。
    /// 分两层是因为网格必须绘制在放大像素之上。
    /// </summary>
    private void EnsureLensChrome()
    {
        if (_lensBack != null) return;

        var back = new Bitmap(LensSize, LensSize, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(back))
            g.Clear(Color.FromArgb(245, 250, 252, 255));
        _lensBack = back;

        var grid = new Bitmap(LensSize, LensSize, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(grid))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;

            using (var gridPen = new Pen(Color.FromArgb(70, 0, 0, 0), 1f))
            {
                for (int i = 1; i < LensGrid; i++)
                {
                    int p = i * LensCell;
                    g.DrawLine(gridPen, p, 0, p, LensSize);
                    g.DrawLine(gridPen, 0, p, LensSize, p);
                }
            }

            int half = LensGrid / 2;
            int cx = half * LensCell + LensCell / 2;
            int cy = cx;

            using (var cross = new Pen(Color.FromArgb(235, 240, 90, 90), 1.6f))
            {
                g.DrawLine(cross, cx - 11, cy, cx - 3, cy);
                g.DrawLine(cross, cx + 3, cy, cx + 11, cy);
                g.DrawLine(cross, cx, cy - 11, cx, cy - 3);
                g.DrawLine(cross, cx, cy + 3, cx, cy + 11);
            }
            using (var centerBrush = new SolidBrush(Color.FromArgb(230, 240, 90, 90)))
                g.FillRectangle(centerBrush, cx - 1, cy - 1, 2, 2);
        }
        _lensGrid = grid;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SETCURSOR)
        {
            // 截图态的光标由会话决定：脏区内"移动"、控制点上"缩放"、胶囊上"手型"。
            if (DesiredCursor is { } cur)
            {
                Cursor.Current = cur;
                m.Result = (IntPtr)1;      // TRUE：已处理，别再让 DefWindowProc 改回去
                return;
            }
        }

        if (m.Msg == WM_DPICHANGED)
        {
            // 忽略系统的 DPI 建议尺寸，保持物理像素定位；
            // 下一帧 Render 会重建位图尺寸并重新同步真实几何。
            SyncWindowMetrics();
            Render(null);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DropDimmed();
            _surface?.Dispose();
            _surface = null;
            _buffer.Dispose();
            _lensBack?.Dispose();
            _lensBack = null;
            _lensGrid?.Dispose();
            _lensGrid = null;
        }
        base.Dispose(disposing);
    }
}
