using iTu.Core;
using iTu.Ui;

namespace iTu;

/// <summary>设置面板：快捷键、保存策略、显示选项。</summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private readonly TextBox _txtCaptureHotkey = new();
    private readonly TextBox _txtFullscreenHotkey = new();
    private readonly TextBox _txtRecordHotkey = new();
    private readonly ComboBox _cmbFormat = new();
    private readonly ComboBox _cmbAfter = new();
    private readonly ComboBox _cmbRecord = new();
    private readonly TextBox _txtDir = new();
    private readonly TextBox _txtPattern = new();
    private readonly CheckBox _chkStartup = new();
    private readonly CheckBox _chkOpenFolder = new();
    private readonly CheckBox _chkHideSelf = new();
    private readonly CheckBox _chkMagnifier = new();
    private readonly CheckBox _chkRuler = new();
    private readonly NumericUpDown _numFps = new();
    private readonly Label _tipLabel = new();

    private readonly ComboBox _cmbTranslateEngine = new();
    private readonly ComboBox _cmbTranslateTarget = new();
    private readonly TextBox _txtAiBaseUrl = new();
    private readonly TextBox _txtAiModel = new();
    private readonly Label _aiTip = new();

    /// <summary>自绘标题栏高度（无边框窗口自己没有标题栏）。</summary>
    private const int TitleHeight = 46;

    private bool _dragging;
    private Point _dragOrigin;
    private Point _windowOrigin;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;   // 与胶囊一致的无边框窗口
        // ShowDialog 没有 owner 时 CenterParent 会退化成左上角，直接用屏幕居中
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Canvas;
        // 高度不能超过屏幕工作区，否则底部按钮会被裁到屏幕外
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        int clientH = Math.Clamp(work.Height - 120, 420, 660);
        ClientSize = new Size(520 + 32, clientH + 32 + TitleHeight);
        Padding = new Padding(16, TitleHeight + 10, 16, 16);   // 内容让出自绘标题栏
        KeyPreview = true;
        Font = Theme.UiFont;
        DoubleBuffered = true;

        // 滚动容器与内容表格分开：
        // 直接把 AutoScroll 加在 Dock=Fill 的 TableLayoutPanel 上时，
        // 滚动范围按表格"首选尺寸"算，最后一两行会被漏掉、滚到底也看不全
        // （表现为设置界面底部内容被裁）。
        var scrollHost = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Surface,
            BackgroundImage = Ui.MenuTexture.Tile(),
            BackgroundImageLayout = ImageLayout.Tile
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(22, 18, 22, 18),
            BackColor = Theme.Surface,
            BackgroundImage = Ui.MenuTexture.Tile(),      // 与菜单同一张纹理
            BackgroundImageLayout = ImageLayout.Tile
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Section(string title)
        {
            var lbl = new Label
            {
                Text = title,
                Font = Theme.UiFontBold,
                ForeColor = Theme.TextSecondary,
                AutoSize = true,
                Margin = new Padding(0, 14, 0, 6)
            };
            root.Controls.Add(lbl, 0, root.RowCount++);

            // 分隔线跨两列，否则只在左列断开、视觉上很别扭
            var line = new Panel { Height = 1, BackColor = Theme.Border, Margin = new Padding(0, 0, 0, 6) };
            int row = root.RowCount++;
            root.Controls.Add(line, 0, row);
            root.SetColumnSpan(line, 2);
        }

        void Row(string label, Control ctrl)
        {
            ctrl.Margin = new Padding(0, 6, 0, 6);
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                ForeColor = Theme.TextSecondary,
                Margin = new Padding(0, 9, 0, 0),

                // 左侧留出图标位；图标用 Paint 自绘 —— Label.Image 不参与文本排版，会和文字叠在一起
                Padding = new Padding(23, 0, 0, 0)
            };
            var glyph = Ui.Glyphs.Small(Ui.Glyphs.ForSetting(label), 16);
            lbl.Paint += (_, e) =>
                e.Graphics.DrawImage(glyph, 0, (lbl.Height - 16) / 2, 16, 16);

            root.Controls.Add(lbl, 0, root.RowCount);
            root.Controls.Add(ctrl, 1, root.RowCount);
            root.RowCount++;
        }

        Section("快捷键");

        _txtCaptureHotkey.Text = _settings.CaptureHotkey;
        _txtCaptureHotkey.ReadOnly = true;
        _txtCaptureHotkey.KeyDown += HotkeyCapture;
        Row("自由截图", _txtCaptureHotkey);

        _txtFullscreenHotkey.Text = _settings.FullscreenHotkey;
        _txtFullscreenHotkey.ReadOnly = true;
        _txtFullscreenHotkey.KeyDown += HotkeyCapture;
        Row("全屏截图", _txtFullscreenHotkey);

        _txtRecordHotkey.Text = _settings.RecordHotkey;
        _txtRecordHotkey.ReadOnly = true;
        _txtRecordHotkey.KeyDown += HotkeyCapture;
        Row("屏幕录制", _txtRecordHotkey);

        _tipLabel.Text = "点击输入框后按下组合键即可设置";
        _tipLabel.AutoSize = true;
        _tipLabel.Font = Theme.SmallFont;
        _tipLabel.ForeColor = Theme.TextMuted;
        root.Controls.Add(_tipLabel, 1, root.RowCount++);

        Section("截图后行为");

        foreach (var c in new[] { _cmbAfter, _cmbFormat, _cmbRecord, _cmbTranslateEngine, _cmbTranslateTarget })
            c.Width = 240;

        _cmbAfter.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbAfter.Items.AddRange(new object[] { "复制到剪贴板", "保存到文件", "复制并保存", "钉在桌面" });
        _cmbAfter.SelectedIndex = (int)_settings.AfterCapture;
        Row("默认动作", _cmbAfter);

        _cmbFormat.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbFormat.Items.AddRange(new object[] { "PNG", "JPG", "BMP" });
        _cmbFormat.SelectedIndex = (int)_settings.ImageFormat;
        Row("图片格式", _cmbFormat);

        _txtPattern.Text = _settings.FileNamePattern;
        _txtPattern.Width = 240;
        Row("文件名模板", _txtPattern);

        _txtDir.Text = _settings.SaveDirectory;
        _txtDir.Width = 264;
        var dirPanel = new Panel { Height = 28, Width = 340 };
        _txtDir.Location = new Point(0, 3);
        var browse = new Ui.RoundedButton
        {
            Text = "浏览…",
            Size = new Size(66, 26),
            Location = new Point(270, 1),
            Style = Ui.RoundedButton.Kind.Ghost
        };
        browse.Font = Theme.SmallFont;
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _txtDir.Text };
            if (dlg.ShowDialog() == DialogResult.OK) _txtDir.Text = dlg.SelectedPath;
        };
        dirPanel.Controls.Add(_txtDir);
        dirPanel.Controls.Add(browse);
        Row("保存目录", dirPanel);

        _chkOpenFolder.Text = "保存后打开所在文件夹";
        _chkOpenFolder.Checked = _settings.OpenFolderAfterSave;
        _chkOpenFolder.Width = 320;
        Row("", _chkOpenFolder);

        Section("录制");

        _cmbRecord.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbRecord.Items.AddRange(new object[] { "GIF", "MP4（需 ffmpeg）" });
        _cmbRecord.SelectedIndex = (int)_settings.RecordFormat;
        Row("录制格式", _cmbRecord);

        _numFps.Minimum = 4;
        _numFps.Maximum = 30;
        _numFps.Value = Math.Clamp(_settings.RecordFps, 4, 30);
        _numFps.Width = 80;
        Row("帧率", _numFps);

        Section("翻译");

        _cmbTranslateEngine.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbTranslateEngine.Items.AddRange(new object[] { "内置在线翻译（免配置）", "开源 AI（OpenAI 兼容接口）" });
        _cmbTranslateEngine.SelectedIndex = (int)_settings.TranslateProvider;
        _cmbTranslateEngine.SelectedIndexChanged += (_, _) => SyncAiFields();
        Row("翻译引擎", _cmbTranslateEngine);

        _cmbTranslateTarget.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbTranslateTarget.Items.AddRange(new object[] { "简体中文", "繁体中文", "英语", "日语", "韩语" });
        _cmbTranslateTarget.SelectedIndex = _settings.TranslateTarget switch
        {
            "zh-TW" => 1,
            "en" => 2,
            "ja" => 3,
            "ko" => 4,
            _ => 0
        };
        Row("目标语言", _cmbTranslateTarget);

        _txtAiBaseUrl.Text = _settings.AiBaseUrl;
        _txtAiBaseUrl.Width = 300;
        Row("AI 接口地址", _txtAiBaseUrl);

        _txtAiModel.Text = _settings.AiModel;
        _txtAiModel.Width = 200;
        Row("AI 模型", _txtAiModel);

        _aiTip.Text = "本地开源模型示例：Ollama 启动后填 http://127.0.0.1:11434/v1，模型填 qwen2.5:7b";
        _aiTip.AutoSize = false;
        _aiTip.Height = 34;
        _aiTip.Width = 320;
        _aiTip.Font = Theme.SmallFont;
        _aiTip.ForeColor = Theme.TextMuted;
        Row("", _aiTip);

        SyncAiFields();

        Section("其他");

        _chkStartup.Text = "开机自动启动";
        _chkStartup.Checked = _settings.RunOnStartup;
        _chkStartup.Width = 320;
        Row("", _chkStartup);

        _chkHideSelf.Text = "截图时隐藏自身窗口";
        _chkHideSelf.Checked = _settings.HideSelfOnCapture;
        _chkHideSelf.Width = 320;
        Row("", _chkHideSelf);

        _chkMagnifier.Text = "选区时显示放大镜";
        _chkMagnifier.Checked = _settings.ShowMagnifier;
        _chkMagnifier.Width = 320;
        Row("", _chkMagnifier);

        _chkRuler.Text = "选区时显示标尺";
        _chkRuler.Checked = _settings.ShowRuler;
        _chkRuler.Width = 320;
        Row("", _chkRuler);

        // 所有行都加完后统一规范行高为 AutoSize，保证表格首选高度等于内容真实高度
        root.RowStyles.Clear();
        for (int i = 0; i < root.RowCount; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        scrollHost.Controls.Add(root);
        Controls.Add(scrollHost);

        // 按钮放进独立的底部栏：用 Dock 布局而不是按 ClientSize 手算坐标，
        // 窗口高度变化时不会跑到可视区之外（旧实现底部被裁的直接原因）。
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Surface };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        var saveBtn = new Ui.RoundedButton
        {
            Text = "保存",
            Size = new Size(88, 32),
            Style = Ui.RoundedButton.Kind.Primary,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 14, 8, 0)
        };
        saveBtn.Click += (_, _) => { Apply(); Close(); };

        var cancelBtn = new Ui.RoundedButton
        {
            Text = "取消",
            Size = new Size(88, 32),
            Style = Ui.RoundedButton.Kind.Ghost,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 14, 22, 0)
        };
        cancelBtn.Click += (_, _) => Close();

        var btnHost = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 206,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface
        };
        btnHost.Controls.Add(cancelBtn);
        btnHost.Controls.Add(saveBtn);

        footer.Controls.Add(btnHost);
        Controls.Add(footer);

        // 自绘标题栏的关闭按钮（去掉系统标题栏后必须自带一个）
        var close = new IconButton
        {
            Icon = IconKind.Close,
            Hint = "关闭",
            Bounds = new Rectangle(ClientSize.Width - 46, 11, 30, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        close.BringToFront();

        // 输入控件统一成胶囊语言：去边框 + 圆角底衬（原生边框无法圆角化）
        Ui.FormStyler.Restyle(root);
    }

    /// <summary>
    /// 背景卡片：与胶囊/菜单同一套 —— 圆角 14、柔影、1px 描边、纹理底图。
    /// 内容面板铺在其上，四周留出 12px 的卡片边。
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Canvas);
        Theme.SetupQuality(g);

        // 无边框窗口：整窗就是一张卡片
        var card = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
        using (var clip = Theme.RoundedRect(card, 16))
        {
            var state = g.Save();
            g.SetClip(clip);
            using (var fill = new SolidBrush(Theme.Surface))
                g.FillRectangle(fill, card);
            g.DrawImage(Ui.MenuTexture.For(new Size((int)card.Width, (int)card.Height)),
                0, 0, card.Width, card.Height);
            g.Restore(state);
        }

        // 卡片描边（圆角）
        using (var path = Theme.RoundedRect(RectangleF.Inflate(card, -0.5f, -0.5f), 16))
        using (var pen = new Pen(Theme.Border, 1f))
            g.DrawPath(pen, path);

        // 标题栏文字
        using (var brush = new SolidBrush(Theme.Text))
            g.DrawString("设置", Theme.Font(11f, FontStyle.Bold), brush, 26, 14);

        base.OnPaint(e);
    }

    /// <summary>无边框窗口：按住标题栏（左上角非控件区域）拖动窗口。</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.Y > TitleHeight) return;

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
        Location = new Point(_windowOrigin.X + (now.X - _dragOrigin.X),
                             _windowOrigin.Y + (now.Y - _dragOrigin.Y));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    /// <summary>Win11 圆角：系统会给无边框窗口裁出真正的抗锯齿圆角。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int pref = 2;   // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
        }
        catch
        {
            // Win10 不支持：退化成直角，不影响功能
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>只有选了开源 AI 引擎时才启用接口地址/模型输入框。</summary>
    private void SyncAiFields()
    {
        bool ai = _cmbTranslateEngine.SelectedIndex == (int)TranslateProvider.OpenSourceAi;
        _txtAiBaseUrl.Enabled = ai;
        _txtAiModel.Enabled = ai;
        _txtAiBaseUrl.BackColor = ai ? Theme.Surface : Theme.SurfaceAlt;
        _txtAiModel.BackColor = ai ? Theme.Surface : Theme.SurfaceAlt;
    }

    private void HotkeyCapture(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        if (e.KeyCode == Keys.Escape) { box.Text = ""; return; }

        var mods = new List<string>();
        if (e.Control) mods.Add("Ctrl");
        if (e.Alt) mods.Add("Alt");
        if (e.Shift) mods.Add("Shift");
        if (e.KeyCode is Keys.LWin or Keys.RWin) mods.Add("Win");

        if (mods.Count == 0) return;

        // 组合键中的主键
        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey) return;

        mods.Add(KeyToString(key));
        box.Text = string.Join("+", mods);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// <summary>
    /// 把主键转成 HotkeyManager.TryParseKey 能识别的单字符 / F 键名。
    /// Keys.ToString() 对数字键返回 "D1"、对标点键返回 "Oemcomma" 这类名字，
    /// 直接塞进去会导致解析失败、热键注册不上，所以这里显式映射回单字符。
    /// </summary>
    private static string KeyToString(Keys key)
    {
        if (key is >= Keys.D0 and <= Keys.D9) return ((char)('0' + (key - Keys.D0))).ToString();
        return key switch
        {
            Keys.Oemtilde          => "`",
            Keys.OemMinus          => "-",
            Keys.Oemplus           => "=",
            Keys.OemOpenBrackets   => "[",
            Keys.OemCloseBrackets  => "]",
            Keys.OemPipe           => "\\",
            Keys.OemSemicolon      => ";",
            Keys.OemQuotes         => "'",
            Keys.Oemcomma          => ",",
            Keys.OemPeriod         => ".",
            Keys.OemQuestion       => "/",
            _                      => key.ToString(),
        };
    }

    private void Apply()
    {
        if (!string.IsNullOrWhiteSpace(_txtCaptureHotkey.Text))
            _settings.CaptureHotkey = _txtCaptureHotkey.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_txtFullscreenHotkey.Text))
            _settings.FullscreenHotkey = _txtFullscreenHotkey.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_txtRecordHotkey.Text))
            _settings.RecordHotkey = _txtRecordHotkey.Text.Trim();

        _settings.AfterCapture = (SaveBehavior)_cmbAfter.SelectedIndex;
        _settings.ImageFormat = (ImageFormatKind)_cmbFormat.SelectedIndex;
        _settings.RecordFormat = (RecordFormat)_cmbRecord.SelectedIndex;
        _settings.FileNamePattern = string.IsNullOrWhiteSpace(_txtPattern.Text) ? "截图_{date}_{time}" : _txtPattern.Text.Trim();
        _settings.SaveDirectory = _txtDir.Text.Trim();
        _settings.OpenFolderAfterSave = _chkOpenFolder.Checked;
        _settings.RunOnStartup = _chkStartup.Checked;
        _settings.HideSelfOnCapture = _chkHideSelf.Checked;
        _settings.ShowMagnifier = _chkMagnifier.Checked;
        _settings.ShowRuler = _chkRuler.Checked;
        _settings.RecordFps = (int)_numFps.Value;

        _settings.TranslateProvider = (TranslateProvider)_cmbTranslateEngine.SelectedIndex;
        _settings.TranslateTarget = _cmbTranslateTarget.SelectedIndex switch
        {
            1 => "zh-TW",
            2 => "en",
            3 => "ja",
            4 => "ko",
            _ => "zh-CN"
        };
        if (!string.IsNullOrWhiteSpace(_txtAiBaseUrl.Text))
            _settings.AiBaseUrl = _txtAiBaseUrl.Text.Trim();
        if (!string.IsNullOrWhiteSpace(_txtAiModel.Text))
            _settings.AiModel = _txtAiModel.Text.Trim();

        _settings.Save();
    }
}
