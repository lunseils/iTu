using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace iTu.Ui;

/// <summary>
/// 把原生输入控件（TextBox / ComboBox）统一成胶囊的视觉语言。
///
/// WinForms 的输入框边框由系统绘制，无法圆角化；而"给每个输入框插一层底衬控件"
/// 会打乱 TableLayoutPanel 的自动布局（实测整页错位）。
/// 所以改为**父面板自绘**：面板画背景时顺带在输入框位置补一圈圆角底衬，
/// 不新增控件、不改布局，输入框压在上面正好落在底衬中间。
/// </summary>
public static class FormStyler
{
    // 不能用静态 HashSet：设置窗每次打开都会新建控件树，
    // 强引用会让所有旧窗体永久留在内存里。弱表随控件一起回收。
    private static readonly ConditionalWeakTable<Control, object> Painted = new();

    /// <summary>对整棵控件树应用统一风格（幂等）。</summary>
    public static void Restyle(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is TextBox tb) StyleTextBox(tb);
            else if (child is ComboBox cb) StyleCombo(cb);

            if (child.Controls.Count > 0) Restyle(child);
        }
    }

    private static void StyleTextBox(TextBox tb)
    {
        tb.BorderStyle = BorderStyle.None;
        tb.BackColor = Theme.SurfaceAlt;
        tb.ForeColor = Theme.Text;
        HookParent(tb);
    }

    private static void StyleCombo(ComboBox cb)
    {
        // ComboBox 的系统边框去不掉，再补底衬会变成"双边框"，这里只做扁平化
        cb.FlatStyle = FlatStyle.Flat;
        cb.BackColor = Theme.SurfaceAlt;
        cb.ForeColor = Theme.Text;
    }

    /// <summary>让父面板绘制时给输入框补圆角底衬（每个父面板只挂一次）。</summary>
    private static void HookParent(Control tb)
    {
        var parent = tb.Parent;
        if (parent == null || Painted.TryGetValue(parent, out _)) return;
        Painted.Add(parent, new object());

        parent.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            foreach (var box in parent.Controls.OfType<TextBox>())
            {
                if (box is not { Visible: true }) continue;

                // 外扩 3px：输入框落在底衬中间，形成"字段卡片"的观感
                var r = new RectangleF(box.Left - 3, box.Top - 2, box.Width + 6, box.Height + 4);
                using var path = Theme.RoundedRect(r, 7);

                using (var fill = new SolidBrush(Theme.SurfaceAlt))
                    g.FillPath(fill, path);

                using var pen = new Pen(box.Focused ? Theme.Accent : Theme.Border, box.Focused ? 1.4f : 1f);
                g.DrawPath(pen, path);
            }
        };
    }
}
