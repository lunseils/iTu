using QqShot.Core;

namespace QqShot.Ui;

/// <summary>
/// 截图界面里的文字标签输入框（覆盖在遮罩层上的轻量输入）。
/// 输入完成后通过回调把文字交给会话创建 <see cref="TextAnnotation"/>。
/// </summary>
public sealed class TextInputForm : Form
{
    private readonly TextBox _box;
    private bool _cancelled;
    private bool _committed;

    public string? Result { get; private set; }

    public TextInputForm(Point screenPos, Color color, float fontSize)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;

        _box = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Font(Math.Max(11f, fontSize), FontStyle.Bold),
            ForeColor = (color.R > 200 && color.G > 200 && color.B > 200) ? Theme.Text : color,
            BackColor = Color.White,
            Location = new Point(4, 4),
            Width = 220,
            Height = 30
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Commit();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                // 必须置取消标志：Close() 会触发 LostFocus，若不加标志，
                // LostFocus 里的 Commit() 会写回 Result，导致"取消"仍然生成文字标注。
                _cancelled = true;
                Close();
            }
        };
        _box.LostFocus += (_, _) => { if (!_cancelled) Commit(); };

        Controls.Add(_box);
        ClientSize = new Size(228, 38);
        Location = screenPos;

        Shown += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    private void Commit()
    {
        if (_cancelled || _committed) return;
        _committed = true;
        Result = string.IsNullOrWhiteSpace(_box.Text) ? null : _box.Text;
        Close();
    }
}
