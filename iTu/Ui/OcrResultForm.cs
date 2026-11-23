using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using iTu.Core;

namespace iTu.Ui;

/// <summary>
/// 文字提取结果面板：左侧原图（仅保留缩小/放大），右侧识别文本（可选中复制 + 一键复制）。
///
/// 视觉参考是双栏深色截图工具：左侧图片区 + 底部缩放条，右侧深色文字区 +
/// 底部剪贴板按钮。右侧文本使用只读 RichTextBox，保留完整的鼠标选择/复制能力。
/// </summary>
public sealed class OcrResultForm : Form
{
    private const int TitleHeight = 52;
    private const int FooterHeight = 80;
    private const double LeftRatio = 0.634;
    private const double MinZoom = 0.1;
    private const double MaxZoom = 4.0;

    private static readonly Color Chrome = Color.FromArgb(31, 31, 31);
    private static readonly Color ImageArea = Color.FromArgb(24, 24, 24);
    private static readonly Color TextArea = Color.FromArgb(30, 30, 30);
    private static readonly Color Footer = Color.FromArgb(27, 27, 27);
    private static readonly Color Divider = Color.FromArgb(58, 58, 58);
    private static readonly Color TextPrimary = Color.FromArgb(215, 215, 215);
    private static readonly Color IconIdle = Color.FromArgb(185, 185, 185);
    private static readonly Color IconHover = Color.FromArgb(245, 245, 245);

    private readonly Bitmap _image;
    private readonly string _content;
    private Bitmap? _display;               // 按当前倍率重采样后的位图（真正改变像素尺寸）
    private double _zoom = 1.0;
    private Point _panOffset;

    private bool _imagePanning;
    private Point _panStart;
    private Point _panScrollOrigin;
    private Point _panOffsetOrigin;

    private readonly Panel _left = new();
    private readonly Panel _right = new();
    private readonly Panel _viewport = new();
    private readonly PictureBox _picture = new();
    private readonly Label _zoomLabel = new();
    private readonly RichTextBox _text = new();
    private readonly CopyButton _copyButton = new();
    private readonly ZoomButton _zoomOut = new(false);
    private readonly ZoomButton _zoomIn = new(true);
    private readonly CaptionButton _minButton = new(CaptionButton.Kind.Minimize);
    private readonly CaptionButton _maxButton = new(CaptionButton.Kind.Maximize);
    private readonly CaptionButton _closeButton = new(CaptionButton.Kind.Close);

    // 右键菜单：在 RightButtonDown 时记住选区，防止原生编辑框把插入点移到点击处
    // 而清掉选区，导致“复制”退化成复制全文。
    private int _menuSelStart = -1;
    private int _menuSelLength = 0;
    private bool _menuOpen;

    private bool _dragging;
    private Point _dragOrigin;
    private Point _windowOrigin;
    private bool _maximized;
    private Rectangle _restoreBounds;

    public OcrResultForm(Image image, string text)
    {
        _image = new Bitmap(image);
        _content = text ?? string.Empty;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Chrome;
        ClientSize = new Size(Math.Min(980, Screen.PrimaryScreen?.WorkingArea.Width ?? 980),
                              Math.Min(640, Screen.PrimaryScreen?.WorkingArea.Height ?? 640));
        MinimumSize = new Size(640, 420);
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "文字提取结果";

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);

        BuildLeftPanel();
        BuildRightPanel();
        BuildCaptionButtons();

        Controls.Add(_left);
        Controls.Add(_right);
        Controls.Add(_minButton);
        Controls.Add(_maxButton);
        Controls.Add(_closeButton);

        Shown += (_, _) => FitImage();
        LayoutContent();
    }

    private void BuildLeftPanel()
    {
        _left.BackColor = Chrome;

        _viewport.Dock = DockStyle.Fill;
        _viewport.BackColor = ImageArea;
        _viewport.AutoScroll = true;
        _viewport.Paint += (_, e) =>
        {
            // 图片比视口小时居中，左右留深色背景（与参考图一致）
            e.Graphics.Clear(ImageArea);
        };
        _viewport.Resize += (_, _) => UpdatePictureSize();
        _viewport.Cursor = Cursors.SizeAll;
        _viewport.MouseDown += BeginImagePan;
        _viewport.MouseMove += MoveImagePan;
        _viewport.MouseUp += EndImagePan;
        _viewport.MouseWheel += ZoomWithWheel;

        _picture.SizeMode = PictureBoxSizeMode.Normal;
        _picture.BackColor = Color.Transparent;
        _picture.Image = _image;   // FitImage 会替换为按倍率重采样的 _display
        _picture.Cursor = Cursors.SizeAll;
        _picture.MouseDown += BeginImagePan;
        _picture.MouseMove += MoveImagePan;
        _picture.MouseUp += EndImagePan;
        _picture.MouseWheel += ZoomWithWheel;
        _viewport.Controls.Add(_picture);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = FooterHeight,
            BackColor = Footer
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Divider);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        _zoomOut.Click += (_, _) => ChangeZoom(-0.15);
        _zoomIn.Click += (_, _) => ChangeZoom(0.15);

        _zoomLabel.Text = "100%";
        _zoomLabel.ForeColor = TextPrimary;
        _zoomLabel.BackColor = Footer;
        _zoomLabel.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
        _zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
        _zoomLabel.Size = new Size(66, 34);

        footer.Controls.Add(_zoomOut);
        footer.Controls.Add(_zoomLabel);
        footer.Controls.Add(_zoomIn);

        _left.Controls.Add(_viewport);
        _left.Controls.Add(footer);
    }

    private void BuildRightPanel()
    {
        _right.BackColor = TextArea;
        _right.Paint += (_, e) =>
        {
            using var pen = new Pen(Divider);
            e.Graphics.DrawLine(pen, 0, 0, 0, _right.Height);
        };

        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = TextArea,
            Padding = new Padding(16, 4, 18, 10)
        };

        var menu = new ContextMenuStrip
        {
            BackColor = Footer,
            ForeColor = TextPrimary,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            RenderMode = ToolStripRenderMode.System
        };
        menu.Items.Add("复制", null, (_, _) => CopySelectionOrAll());
        menu.Items.Add("全选", null, (_, _) => { _text.Focus(); _text.SelectAll(); });
        // 菜单弹出/关闭时维护状态：Esc 关闭菜单时不要连带关闭整个结果面板。
        menu.Opening += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            // 任何关闭方式（Esc / 点别处 / 键盘触发）都要复位右键选区缓存，
            // 否则下一次用 Shift+F10 打开菜单会用到上一次的过时选区。
            _menuSelStart = -1;
            _menuSelLength = 0;
        };

        _text.Dock = DockStyle.Fill;
        _text.ReadOnly = true;
        _text.BorderStyle = BorderStyle.None;
        _text.BackColor = TextArea;
        _text.ForeColor = TextPrimary;
        _text.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        _text.ScrollBars = RichTextBoxScrollBars.Vertical;
        _text.WordWrap = true;
        _text.DetectUrls = false;
        _text.ShortcutsEnabled = true;
        _text.HideSelection = false;
        _text.ContextMenuStrip = menu;
        _text.Text = _content;
        _text.SelectionStart = 0;
        _text.SelectionLength = 0;

        // 右键弹出的瞬间先记下当前选区。原生 RichTextBox 在右键按下时若点击位置
        // 落在选区外，会把插入点移动到该处并清空选区，导致随后“复制”拿到空串、
        // 退化成复制全文 —— 这是“选取菜单复制不对”的根因。
        _text.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            _menuSelStart = _text.SelectionStart;
            _menuSelLength = _text.SelectionLength;
        };

        host.Controls.Add(_text);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = FooterHeight,
            BackColor = Footer
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Divider);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        _copyButton.Click += (_, _) => CopyAll();
        footer.Controls.Add(_copyButton);

        _right.Controls.Add(host);
        _right.Controls.Add(footer);
    }

    private void BuildCaptionButtons()
    {
        foreach (var button in new Control[] { _minButton, _maxButton, _closeButton })
        {
            button.Size = new Size(44, 32);
            button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        }

        int right = ClientSize.Width - 12;
        _closeButton.Location = new Point(right - 44, 10);
        _maxButton.Location = new Point(right - 88, 10);
        _minButton.Location = new Point(right - 132, 10);

        _minButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _maxButton.Click += (_, _) => ToggleMaximize();
        _closeButton.Click += (_, _) => Close();

        var tip = new ToolTip();
        tip.SetToolTip(_minButton, "最小化");
        tip.SetToolTip(_maxButton, "最大化");
        tip.SetToolTip(_closeButton, "关闭");
    }

    private void LayoutContent()
    {
        int split = (int)Math.Round(ClientSize.Width * LeftRatio);
        _left.SetBounds(0, TitleHeight, split, Math.Max(0, ClientSize.Height - TitleHeight));
        _right.SetBounds(split, TitleHeight, Math.Max(0, ClientSize.Width - split),
                         Math.Max(0, ClientSize.Height - TitleHeight));

        // 缩放控件组在图片正下方居中（图片本身在视口里也是居中的）
        const int zoomGap = 8;
        int zoomGroupWidth = _zoomOut.Width + zoomGap + _zoomLabel.Width + zoomGap + _zoomIn.Width;
        int zoomX = Math.Max(0, (_left.Width - zoomGroupWidth) / 2);
        int zoomY = (FooterHeight - _zoomOut.Height) / 2;
        _zoomOut.Location = new Point(zoomX, zoomY);
        _zoomLabel.Location = new Point(zoomX + _zoomOut.Width + zoomGap, zoomY);
        _zoomIn.Location = new Point(zoomX + _zoomOut.Width + zoomGap + _zoomLabel.Width + zoomGap, zoomY);

        _copyButton.Location = new Point((_right.Width - _copyButton.Width) / 2,
                                         (FooterHeight - _copyButton.Height) / 2);
        UpdatePictureSize();
    }

    private void FitImage()
    {
        int availW = Math.Max(80, _viewport.ClientSize.Width - 48);
        int availH = Math.Max(80, _viewport.ClientSize.Height - 48);
        _zoom = Math.Min(availW / (double)_image.Width, availH / (double)_image.Height);
        _zoom = Math.Clamp(_zoom, MinZoom, MaxZoom);
        _panOffset = Point.Empty;
        _viewport.AutoScrollPosition = Point.Empty;
        UpdatePictureSize();
    }

    /// <summary>缩放。anchor 是视口内的锚点，滚轮缩放时保持光标下的图像位置不动。</summary>
    private void ChangeZoom(double delta, Point? anchor = null)
    {
        double oldZoom = _zoom;
        double newZoom = Math.Clamp(oldZoom + delta, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.000001) return;

        if (anchor.HasValue && oldZoom > 0)
        {
            int oldW = Math.Max(1, (int)Math.Round(_image.Width * oldZoom));
            int oldH = Math.Max(1, (int)Math.Round(_image.Height * oldZoom));
            int oldBaseX = Math.Max(0, (_viewport.ClientSize.Width - oldW) / 2);
            int oldBaseY = Math.Max(0, (_viewport.ClientSize.Height - oldH) / 2);

            float imgX = (anchor.Value.X - oldBaseX - _panOffset.X) / (float)oldZoom;
            float imgY = (anchor.Value.Y - oldBaseY - _panOffset.Y) / (float)oldZoom;

            _zoom = newZoom;
            int newW = Math.Max(1, (int)Math.Round(_image.Width * _zoom));
            int newH = Math.Max(1, (int)Math.Round(_image.Height * _zoom));
            int newBaseX = Math.Max(0, (_viewport.ClientSize.Width - newW) / 2);
            int newBaseY = Math.Max(0, (_viewport.ClientSize.Height - newH) / 2);

            _panOffset = new Point(
                (int)Math.Round(anchor.Value.X - newBaseX - imgX * _zoom),
                (int)Math.Round(anchor.Value.Y - newBaseY - imgY * _zoom));
        }
        else
        {
            _zoom = newZoom;
        }

        UpdatePictureSize();
    }

    private void BeginImagePan(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _imagePanning = true;
        _panStart = _viewport.PointToClient(Cursor.Position);
        _panScrollOrigin = _viewport.AutoScrollPosition;
        _panOffsetOrigin = _panOffset;
        (sender as Control)?.Capture = true;
    }

    private void MoveImagePan(object? sender, MouseEventArgs e)
    {
        if (!_imagePanning) return;
        var p = _viewport.PointToClient(Cursor.Position);
        int dx = p.X - _panStart.X;
        int dy = p.Y - _panStart.Y;

        // 有滚动条时优先滚动；滚到边界后剩余位移再移动图片本身，保证“随意拖动”。
        _viewport.AutoScrollPosition = new Point(_panScrollOrigin.X + dx, _panScrollOrigin.Y + dy);
        var actual = _viewport.AutoScrollPosition;
        int leftoverX = dx - (actual.X - _panScrollOrigin.X);
        int leftoverY = dy - (actual.Y - _panScrollOrigin.Y);
        _panOffset = new Point(_panOffsetOrigin.X + leftoverX, _panOffsetOrigin.Y + leftoverY);
        UpdatePictureSize();
    }

    private void EndImagePan(object? sender, MouseEventArgs e)
    {
        if (!_imagePanning) return;
        _imagePanning = false;
        (sender as Control)?.Capture = false;
    }

    private void ZoomWithWheel(object? sender, MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        var anchor = _viewport.PointToClient(Cursor.Position);
        ChangeZoom(e.Delta > 0 ? 0.1 : -0.1, anchor);
    }

    private void UpdatePictureSize()
    {
        if (_picture.Image == null || _viewport.ClientSize.Width <= 0 || _viewport.ClientSize.Height <= 0) return;

        int w = Math.Max(1, (int)Math.Round(_image.Width * _zoom));
        int h = Math.Max(1, (int)Math.Round(_image.Height * _zoom));

        // 真正按倍率重采样：PictureBox 用 Normal 显示，位图本身像素尺寸随缩放变化。
        // 这样放大能看清像素，缩小能看到整图，而不是只裁掉显示区域。
        if (_display == null || _display.Width != w || _display.Height != h)
        {
            var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.Clear(ImageArea);
                // 缩放统一走高质量双三次插值，缩小看整图、放大保持平滑（OCR 截图放大后文字边缘仍清晰）。
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(_image, new Rectangle(0, 0, w, h),
                            0, 0, _image.Width, _image.Height, GraphicsUnit.Pixel);
            }
            _picture.Image = scaled;
            _display?.Dispose();
            _display = scaled;
        }

        _picture.Size = new Size(w, h);

        int baseX = Math.Max(0, (_viewport.ClientSize.Width - w) / 2);
        int baseY = Math.Max(0, (_viewport.ClientSize.Height - h) / 2);
        _picture.Location = new Point(baseX + _panOffset.X, baseY + _panOffset.Y);
        _zoomLabel.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void CopyAll()
    {
        try
        {
            if (!string.IsNullOrEmpty(_content)) Clipboard.SetText(_content);
            _copyButton.FlashCopied();
        }
        catch (Exception ex)
        {
            Log.Info("[ocr-result] 一键复制失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 右键“复制”：复制用户选中的文字；没有选区时才回退到全文。
    /// 优先用右键按下时缓存的选区 —— 菜单弹出会令 RichTextBox 失去焦点并可能
    /// 清掉高亮选区，直接读 SelectedText 会拿到空串而错误地复制全文。
    /// </summary>
    private void CopySelectionOrAll()
    {
        try
        {
            string text = string.Empty;

            // 1) 右键按下瞬间缓存的选区（最可靠）
            if (_menuSelLength > 0 &&
                _menuSelStart >= 0 &&
                _menuSelStart + _menuSelLength <= _text.TextLength)
            {
                text = _text.Text.Substring(_menuSelStart, _menuSelLength);
            }

            // 2) 当前仍保留的实时选区（键盘 Shift+F10 / 菜单键触发时走这里）
            if (text.Length == 0 && _text.SelectionLength > 0)
                text = _text.SelectedText;

            // 3) 都没有选区，才退回全文
            if (text.Length == 0) text = _content;

            if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
            _copyButton.FlashCopied();
        }
        catch (Exception ex)
        {
            Log.Info("[ocr-result] 复制选中文本失败: " + ex.Message);
        }
        finally
        {
            _menuSelStart = -1;
            _menuSelLength = 0;
        }
    }

    private void ToggleMaximize()
    {
        if (!_maximized)
        {
            _restoreBounds = Bounds;
            Bounds = Screen.FromHandle(Handle).WorkingArea;
            _maximized = true;
        }
        else
        {
            Bounds = _restoreBounds;
            _maximized = false;
        }
        _maxButton.Refresh();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        LayoutContent();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Chrome);
        base.OnPaint(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.Y > TitleHeight || _maximized) return;
        _dragging = true;
        _dragOrigin = Cursor.Position;
        _windowOrigin = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var now = Cursor.Position;
        Location = new Point(_windowOrigin.X + now.X - _dragOrigin.X,
                             _windowOrigin.Y + now.Y - _dragOrigin.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Y <= TitleHeight && e.Button == MouseButtons.Left) ToggleMaximize();
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            // 右键菜单打开时，Esc 交给菜单关闭自己；若在这里抢先关闭窗体，
            // 用户只是想取消菜单却会把整个结果面板关掉。
            if (_menuOpen) return false;
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image.Dispose();
            _display?.Dispose();
            _text.ContextMenuStrip?.Dispose();
            _copyButton.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>底部“一键复制”按钮：剪贴板图标 + 文案，悬停/复制后有反馈。</summary>
    private sealed class CopyButton : Control
    {
        private bool _hover;
        private bool _copied;
        private readonly System.Windows.Forms.Timer _timer;

        public CopyButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(104, 34);
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
            ForeColor = TextPrimary;
            BackColor = Footer;

            // 只订阅一次：若放在 FlashCopied 里，每次复制都会累加一个 Tick handler，
            // N 次复制后一次 Tick 触发 N 次，造成闪烁与内存增长。
            _timer = new System.Windows.Forms.Timer { Interval = 1400 };

            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                _copied = false;
                Invalidate();
            };
        }

        public void FlashCopied()
        {
            _copied = true;
            Invalidate();
            _timer.Stop();
            _timer.Start();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            string label = _copied ? "已复制" : "一键复制";
            SizeF textSize = g.MeasureString(label, Font);
            int iconW = 18, gap = 10;
            int total = iconW + gap + (int)Math.Ceiling(textSize.Width);
            int x = Math.Max(0, (Width - total) / 2);
            int y = (Height - 18) / 2;
            var ink = _hover || _copied ? IconHover : IconIdle;

            using var pen = new Pen(ink, 1.3f);
            // 剪贴板主体
            g.DrawRectangle(pen, x + 1, y + 3, 14, 15);
            // 顶部夹子
            g.DrawRectangle(pen, x + 5, y, 6, 4);
            g.DrawLine(pen, x + 1, y + 7, x + 15, y + 7);

            using var brush = new SolidBrush(ink);
            g.DrawString(label, Font, brush, x + iconW + gap, (Height - textSize.Height) / 2 - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>图片下方的缩小/放大镜按钮（参考图样式）。</summary>
    private sealed class ZoomButton : Control
    {
        private readonly bool _plus;
        private bool _hover;

        public ZoomButton(bool plus)
        {
            _plus = plus;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(36, 36);
            Cursor = Cursors.Hand;
            BackColor = Footer;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            var ink = _hover ? IconHover : IconIdle;

            // 手柄：圆头矢量线，起点放在镜片边缘之外，避免穿进镜片内部
            using (var handle = new Pen(ink, 3.2f)
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round
                   })
            {
                g.DrawLine(handle, 23.2f, 23.2f, 29f, 29f);
            }
            using (var shine = new Pen(Color.FromArgb(90, 255, 255, 255), 0.9f)
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round
                   })
            {
                g.DrawLine(shine, 24f, 23.6f, 28f, 27.6f);
            }

            // 镜片：不透明底色遮住手柄可能伸入的部分，再画外圈/内圈/高光弧
            using (var lensFill = new SolidBrush(BackColor))
                g.FillEllipse(lensFill, 4.2f, 4.2f, 19.6f, 19.6f);

            using (var lens = new Pen(ink, 1.8f))
                g.DrawEllipse(lens, 4.2f, 4.2f, 19.6f, 19.6f);

            using (var inner = new Pen(Color.FromArgb(70, ink), 0.8f))
                g.DrawEllipse(inner, 6f, 6f, 16f, 16f);

            using (var arc = new Pen(Color.FromArgb(110, 255, 255, 255), 1f)
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round
                   })
            {
                g.DrawArc(arc, 5.2f, 5.2f, 17.6f, 17.6f, 198f, 62f);
            }

            // 镜片内的 + / −：同样用圆头线，避免锯齿和方角
            using var sign = new Pen(ink, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(sign, 9f, 14f, 19f, 14f);
            if (_plus) g.DrawLine(sign, 14f, 9f, 14f, 19f);
        }
    }

    /// <summary>自绘最小化/最大化/关闭按钮（参考图右上角）。</summary>
    private sealed class CaptionButton : Control
    {
        internal enum Kind { Minimize, Maximize, Close }

        private readonly Kind _kind;
        private bool _hover;

        public CaptionButton(Kind kind)
        {
            _kind = kind;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            bool closeHover = _kind == Kind.Close && _hover;
            g.Clear(closeHover ? Color.FromArgb(232, 17, 35) : (_hover ? Color.FromArgb(58, 58, 58) : Chrome));
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var pen = new Pen(closeHover ? Color.White : IconIdle, 1.3f);
            int cx = Width / 2, cy = Height / 2;
            switch (_kind)
            {
                case Kind.Minimize:
                    g.DrawLine(pen, cx - 6, cy + 3, cx + 6, cy + 3);
                    break;
                case Kind.Maximize:
                    g.DrawRectangle(pen, cx - 6, cy - 6, 12, 12);
                    break;
                case Kind.Close:
                    g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                    g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                    break;
            }
        }
    }
}
