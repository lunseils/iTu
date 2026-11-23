using System.Drawing.Drawing2D;

namespace iTu.Ui;

/// <summary>
/// 胶囊工具栏的**纯绘制 + 命中**逻辑。
///
/// 遮罩层（截图界面）不再弹独立的胶囊窗口，而是把胶囊直接画在
/// 自己的 <see cref="OverlayForm"/> 上 —— 与圆角控件、扩拉线、选区框同处
/// **一个图层**。这个类就是把 <see cref="PillToolbar"/> 的绘制内容
/// 提出来，让它能画到任意 Graphics 上。
/// </summary>
public static class PillChrome
{
    public static int Count => PillToolbar.Cells.Length;

    public static PillAction ActionAt(int index) => PillToolbar.Cells[index].Action;

    /// <summary>悬停提示文案（与旧胶囊工具栏一致）。</summary>
    internal static string? HintOf(PillAction action) => PillToolbar.HintOf(action);

    /// <summary>在目标区域（本地坐标从 0,0 起）绘制整条胶囊。</summary>
    public static void Draw(Graphics g, Rectangle bounds, int hover = -1)
    {
        // 胶囊边缘全是曲线（左右两端是半径=半高的圆弧），GDI+ 的 1:1 抗锯齿
        // （8x4 网格）会在白面与深色遮罩的交界留下台阶感。这里局部超采样 2 倍，
        // 复刻 ituk 的平滑圆角观感。
        Theme.DrawSupersampled(g, bounds, bg => DrawCore(bg, bounds, hover), factor: 2, margin: 8);
    }

    private static void DrawCore(Graphics g, Rectangle bounds, int hover)
    {
        float s = bounds.Width / (float)PillToolbar.BaseSize.Width;

        var rect = new RectangleF(0.5f, 0.5f, bounds.Width - 1f, bounds.Height - 1f);
        float radius = rect.Height / 2f;

        Theme.DrawSoftShadow(g, rect, radius, 7, 26);
        Theme.FillRounded(g, rect, radius, Theme.Surface, Theme.Border);

        if (hover >= 0) FillCell(g, hover, s, bounds.Height, Theme.CapsuleHover);

        for (int i = 0; i < PillToolbar.Cells.Length; i++)
        {
            var action = PillToolbar.Cells[i].Action;

            // 与编辑器胶囊同一组颜色：普通墨色、取消红、确认绿
            var baseColor = action switch
            {
                PillAction.Close => Theme.PillClose,
                PillAction.Confirm => Theme.PillConfirm,
                _ => Theme.PillInk
            };

            var design = LegacyIcons.DesignSize(PillToolbar.IconOf(action));
            float w = design.Width * s, h = design.Height * s;

            var box = new RectangleF(
                PillToolbar.Cells[i].CenterX * s - w / 2f,
                bounds.Height / 2f - h / 2f,
                w, h);

            LegacyIcons.Draw(g, PillToolbar.IconOf(action), box, baseColor);
        }
    }

    private static void FillCell(Graphics g, int index, float s, int height, Color color)
    {
        var r = CellRect(index, s, height);
        r.Inflate(-3, -4);
        if (r.Width <= 0 || r.Height <= 0) return;

        using var path = Theme.RoundedRect(r, 8);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>格位矩形（以胶囊左上为原点）。</summary>
    public static Rectangle CellRect(int index, float s, int height)
    {
        var cells = PillToolbar.Cells;

        float left = index == 0
            ? cells[0].CenterX * s - 20 * s
            : (cells[index - 1].CenterX + cells[index].CenterX) / 2f * s;
        float right = index == cells.Length - 1
            ? cells[^1].CenterX * s + 20 * s
            : (cells[index].CenterX + cells[index + 1].CenterX) / 2f * s;

        int l = (int)Math.Round(left);
        return new Rectangle(l, 0, Math.Max(1, (int)Math.Round(right) - l), height);
    }

    /// <summary>坐标（相对胶囊左上）→ 格位序号；-1 未命中。</summary>
    public static int HitTest(Rectangle bounds, Point p)
    {
        if (p.X < 0 || p.X >= bounds.Width || p.Y < 0 || p.Y >= bounds.Height) return -1;

        float s = bounds.Width / (float)PillToolbar.BaseSize.Width;
        for (int i = 0; i < PillToolbar.Cells.Length; i++)
        {
            var r = CellRect(i, s, bounds.Height);
            if (p.X >= r.Left && p.X < r.Right) return i;
        }
        return -1;
    }
}
