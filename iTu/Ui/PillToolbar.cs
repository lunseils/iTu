namespace iTu.Ui;

/// <summary>胶囊条上的功能格位（顺序与参考图一致）。</summary>
public enum PillAction
{
    None = 0,
    Avatar,      // 保留枚举值（旧配置可能引用），实际用 Share
    Share,       // 最左的人像图标：转发
    Shape,
    Arrow,
    Pen,
    Text,
    Stipple,
    Pin,
    Translate,
    Ocr,
    Undo,
    Redo,
    Save,
    Close,
    Confirm
}

/// <summary>
/// 胶囊工具栏的共享元数据（基准尺寸、格位、图标名、悬停文案）。
///
/// 截图态的胶囊已改为画在 OverlayForm 的同一张分层位图里，
/// 绘制与命中由 <see cref="PillChrome"/> 负责；这里不再是可实例化的 Form。
/// </summary>
public static class PillToolbar
{
    /// <summary>参考图中胶囊的原始尺寸，作为 1:1 基准。</summary>
    public static readonly Size BaseSize = new(595, 38);

    /// <summary>各格位在基准宽度上的中心位置（按参考图实测）。</summary>
    internal static readonly (PillAction Action, int CenterX)[] Cells =
    {
        (PillAction.Shape,     20),
        (PillAction.Arrow,     59),
        (PillAction.Pen,       96),
        (PillAction.Text,     143),
        (PillAction.Stipple,  213),
        (PillAction.Pin,      256),
        // 「文字提取」与「翻译」按用户要求互换位置（识别在前、翻译在后）。
        // 数组必须保持 CenterX 升序 —— CellRect 用相邻两项取中点算格位宽度。
        (PillAction.Ocr,      294),
        (PillAction.Translate,332),
        (PillAction.Undo,     388),
        (PillAction.Redo,     427),
        (PillAction.Share,    464),
        (PillAction.Save,     501),
        (PillAction.Close,    539),
        (PillAction.Confirm,  578),
    };

    private static readonly Dictionary<PillAction, string> Hints = new()
    {
        [PillAction.Share] = "转发",
        [PillAction.Shape] = "形状",
        [PillAction.Arrow] = "箭头",
        [PillAction.Pen] = "画笔",
        [PillAction.Text] = "文字",
        [PillAction.Stipple] = "马赛克",
        [PillAction.Pin] = "贴图（钉在桌面）",
        [PillAction.Translate] = "翻译",
        [PillAction.Ocr] = "文字提取",
        [PillAction.Undo] = "撤销",
        [PillAction.Redo] = "前进",
        [PillAction.Save] = "保存",
        [PillAction.Close] = "取消",
        [PillAction.Confirm] = "复制并关闭",
    };

    /// <summary>格位 → 旧版图标名（数据见 <see cref="LegacyIcons"/>）。</summary>
    internal static string IconOf(PillAction action) => action switch
    {
        PillAction.Shape => "shape",
        PillAction.Arrow => "arrow",
        PillAction.Pen => "pen",
        PillAction.Text => "text",
        PillAction.Stipple => "stipple",
        PillAction.Pin => "pin",
        PillAction.Translate => "translate",
        PillAction.Ocr => "ocr",
        PillAction.Undo => "undo",
        PillAction.Redo => "redo",
        PillAction.Share => "share",
        PillAction.Save => "save",
        PillAction.Close => "close",
        PillAction.Confirm => "confirm",
        _ => "shape"
    };

    /// <summary>悬停提示文案；无文案的保留枚举返回 null。</summary>
    internal static string? HintOf(PillAction action) =>
        Hints.TryGetValue(action, out var hint) ? hint : null;
}
