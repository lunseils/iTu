using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using iTu.Core;

namespace iTu.Ui;

/// <summary>
/// 钉在桌面：置顶小窗，可拖动、复制、关闭。
///
/// 所有权约定：<see cref="PinWindow"/> 接管传入位图并在窗口关闭时释放，
/// 调用方**不得**再对它 using/Dispose。
/// （旧实现在调用处写 using 把图提前释放，导致钉图窗显示异常。）
/// </summary>
public sealed class PinWindow : Form
{
    private const int HeaderHeight = 30;
    private const int CaptionHeight = 26;

    private Bitmap? _image;
    private readonly Panel _imageHost;
    private bool _dragging;
    private Point _dragOrigin;
    private Point _windowOrigin;

    public PinWindow(Bitmap image)
    {
        _image = image;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Surface;
        DoubleBuffered = true;

        int viewW = Math.Max(1, Math.Min(image.Width, 720));
        int viewH = Math.Max(1, Math.Min(image.Height, 520));

        ClientSize = new Size(viewW, HeaderHeight + viewH + CaptionHeight);

        // 图片显示区（自适应等比缩放）
        _imageHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas
        };
        _imageHost.Paint += OnImageHostPaint;
        _imageHost.Resize += (_, _) => _imageHost.Invalidate();

        // 底部说明
        var caption = new Label
        {
            Dock = DockStyle.Bottom,
            Height = CaptionHeight,
            Text = $"已钉住  {image.Width}×{image.Height}   ·   拖动标题栏移动",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            BackColor = Theme.Surface
        };

        // 顶部工具条
        var header = new Panel { Dock = DockStyle.Top, Height = HeaderHeight, BackColor = Theme.Surface };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var closeBtn = new IconButton
        {
            Icon = IconKind.Close,
            Size = new Size(26, 26),
            Margin = new Padding(2, 2, 2, 0),
            Danger = true
        };
        closeBtn.Click += (_, _) => Close();

        var copyBtn = new IconButton
        {
            Icon = IconKind.Copy,
            Size = new Size(26, 26),
            Margin = new Padding(2, 2, 2, 0)
        };
        copyBtn.Click += (_, _) =>
        {
            if (_image != null) ImageUtil.CopyToClipboard(_image);
            Toast.Show(this, "已复制到剪贴板", Theme.Success);
        };

        // 用 Dock 布局而不是 Anchor：
        // Anchor 的偏移量在面板还是默认宽度时就被记录，
        // 停靠到实际宽度后按钮会被推出可视区（旧版按钮"消失"的原因）。
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 64,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface
        };
        buttons.Controls.Add(closeBtn);
        buttons.Controls.Add(copyBtn);

        var title = new Label
        {
            Text = "钉图",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextMuted,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        };

        header.Controls.Add(title);
        header.Controls.Add(buttons);

        // 拖动：标题栏按住拖、松开停
        AttachMove(header);
        AttachMove(title);

        // 停靠顺序：后加的先生效。header 顶部、caption 底部、图片填充剩余区域
        Controls.Add(_imageHost);
        Controls.Add(caption);
        Controls.Add(header);

        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            Math.Max(work.Left + 8, work.Right - Width - 40),
            Math.Max(work.Top + 8, work.Bottom - Height - 40));
    }

    private Control? _moveCapture;
    private System.Windows.Forms.Timer? _moveWatchdog;

    /// <summary>
    /// 标题栏拖动。捕获必须设在**收到按下的控件**上，
    /// 设在窗体上时指针一离开 30px 高的标题栏就收不到 MouseMove，
    /// 窗口移几像素就卡住（编辑器的标题栏原本有同样的缺陷）。
    /// </summary>
    private void AttachMove(Control target)
    {
        target.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;

            _dragging = true;
            _dragOrigin = Cursor.Position;
            _windowOrigin = Location;

            _moveCapture = s as Control ?? target;
            _moveCapture.Capture = true;

            if (_moveWatchdog == null)
            {
                _moveWatchdog = new System.Windows.Forms.Timer { Interval = 120 };
                _moveWatchdog.Tick += (_, _) =>
                {
                    if (_dragging && !Native.IsLeftButtonDown()) EndMove();
                };
            }
            _moveWatchdog.Start();
        };

        target.MouseMove += (_, _) => DoMove();
        target.MouseUp += (_, _) => EndMove();
    }

    private void DoMove()
    {
        if (!_dragging) return;
        var now = Cursor.Position;
        Location = new Point(_windowOrigin.X + (now.X - _dragOrigin.X),
                             _windowOrigin.Y + (now.Y - _dragOrigin.Y));
    }

    private void EndMove()
    {
        if (!_dragging) return;

        _dragging = false;
        _moveWatchdog?.Stop();

        if (_moveCapture != null)
        {
            _moveCapture.Capture = false;
            _moveCapture = null;
        }
    }

    private void OnImageHostPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.SetupQuality(g);
        g.Clear(Theme.Canvas);

        if (_image == null) return;

        // 等比缩放并居中，留 6px 内边距
        var avail = new Rectangle(6, 6, Math.Max(1, _imageHost.Width - 12), Math.Max(1, _imageHost.Height - 12));
        float scale = Math.Min(avail.Width / (float)_image.Width, avail.Height / (float)_image.Height);
        if (scale <= 0) return;

        int w = Math.Max(1, (int)(_image.Width * scale));
        int h = Math.Max(1, (int)(_image.Height * scale));
        var dst = new Rectangle(avail.X + (avail.Width - w) / 2, avail.Y + (avail.Height - h) / 2, w, h);

        g.InterpolationMode = scale < 1f ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
        g.DrawImage(_image, dst);
        using var pen = new Pen(Theme.Border);
        g.DrawRectangle(pen, dst.X, dst.Y, dst.Width - 1, dst.Height - 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // 圆角外框（内容区由子控件自行绘制，这里只描边）
        using var pen = new Pen(Theme.Border);
        g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dragging = false;
            _moveWatchdog?.Stop();
            _moveWatchdog?.Dispose();
            _moveWatchdog = null;
            _image?.Dispose();
            _image = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>识别/翻译结果的文本面板。</summary>
public sealed class TextResultPanel : Form
{
    private readonly TextBox _box;

    public TextResultPanel(string title, string content)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Surface;
        ClientSize = new Size(520, 420);
        DoubleBuffered = true;

        var header = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.Surface };
        var titleLabel = new Label
        {
            Text = title,
            Font = Theme.UiFontBold,
            ForeColor = Theme.Text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0)
        };

        var closeBtn = new IconButton
        {
            Icon = IconKind.Close,
            Size = new Size(26, 26),
            Margin = new Padding(2, 4, 8, 0)
        };
        closeBtn.Click += (_, _) => Close();

        // Dock 布局，避免 Anchor 在面板尚未定宽时算错位置
        var btnHost = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 42,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface
        };
        btnHost.Controls.Add(closeBtn);

        header.Controls.Add(titleLabel);
        header.Controls.Add(btnHost);

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = Theme.UiFont,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Text = content,
            ScrollBars = ScrollBars.Vertical
        };
        _box = box;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Theme.Surface };
        var copyBtn = new Button
        {
            Text = "复制",
            Size = new Size(88, 30),
            Location = new Point(ClientSize.Width - 104, 11),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.UiFont,
            BackColor = Theme.Accent,
            ForeColor = Theme.TextOnAccent,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom
        };
        copyBtn.FlatAppearance.BorderSize = 0;
        copyBtn.Click += (_, _) =>
        {
            try
            {
                // 必须取当前文本框内容：构造时的 content 只是"识别中…"占位符，
                // 异步结果回填后复制旧值会拿到空/占位文本。
                string text = _box.IsDisposed ? "" : _box.Text;
                System.Windows.Forms.Clipboard.SetText(text);
                Toast.Show(this, "已复制文本", Theme.Success);
            }
            catch (Exception ex)
            {
                Log.Info("[text-panel] 复制失败: " + ex.Message);
                Toast.Show(this, "复制失败：" + ex.Message, Theme.Danger);
            }
        };

        var saveBtn = new Button
        {
            Text = "下载",
            Size = new Size(88, 30),
            Location = new Point(ClientSize.Width - 200, 11),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.UiFont,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextSecondary,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom
        };
        saveBtn.FlatAppearance.BorderColor = Theme.Border;
        saveBtn.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "文本文件|*.txt",
                FileName = "文字提取.txt",
                InitialDirectory = AppSettings.Load().SaveDirectory
            };
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    string text = _box.IsDisposed ? "" : _box.Text;
                    File.WriteAllText(dlg.FileName, text, System.Text.Encoding.UTF8);
                    Toast.Show(this, "已保存", Theme.Success);
                }
                catch (Exception ex)
                {
                    Toast.Show(this, "保存失败：" + ex.Message, Theme.Danger);
                }
            }
        };

        footer.Controls.Add(copyBtn);
        footer.Controls.Add(saveBtn);

        Controls.Add(box);
        Controls.Add(header);
        Controls.Add(footer);
    }

    /// <summary>异步更新内容（用于 OCR 结果回填）。</summary>
    public void SetContent(string content)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() =>
            {
                // 回调排队期间窗口可能已被关闭
                if (!IsDisposed && !_box.IsDisposed) _box.Text = content;
            }));
            return;
        }
        _box.Text = content;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.SetupQuality(e.Graphics);
        Theme.PaintCard(e.Graphics, ClientSize, Theme.RadiusLarge);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Theme.ApplyRoundedRegion(this, Theme.RadiusLarge);

        if (Owner is Form owner)
        {
            Location = new Point(
                owner.Left + (owner.Width - Width) / 2,
                owner.Top + (owner.Height - Height) / 2);
            return;
        }

        // 没有 owner 时（托盘「文字提取」那条路径）必须自己定位。
        // StartPosition = Manual 且不设 Location 会停在 (0,0) —— 也就是屏幕左上角，
        // 表现为"识别完在角落里冒出一个窗口"。这里改成居中于光标所在显示器。
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            area.Top + (area.Height - Height) / 2);
    }
}
