using System.Text.Json;
using System.Text.Json.Serialization;

namespace iTu.Core;

public sealed class AppSettings
{
    private const string ProductName = "iTu";

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName);

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>自由截图（拖拽框选）热键。</summary>
    public string CaptureHotkey { get; set; } = "Ctrl+Alt+A";

    /// <summary>全屏截图热键（不框选，直接抓整块虚拟桌面）。</summary>
    public string FullscreenHotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>屏幕录制热键。</summary>
    public string RecordHotkey { get; set; } = "Ctrl+Shift+F";

    /// <summary>截图后默认动作。</summary>
    public SaveBehavior AfterCapture { get; set; } = SaveBehavior.CopyToClipboard;

    /// <summary>保存目录。</summary>
    public string SaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), ProductName);

    /// <summary>图片格式。</summary>
    public ImageFormatKind ImageFormat { get; set; } = ImageFormatKind.Png;

    /// <summary>文件名模板，支持 {time} {date} {n}。</summary>
    public string FileNamePattern { get; set; } = "截图_{date}_{time}";

    /// <summary>保存后不弹出文件夹。</summary>
    public bool OpenFolderAfterSave { get; set; } = false;

    /// <summary>启动时自动运行。</summary>
    public bool RunOnStartup { get; set; } = false;

    /// <summary>截图时隐藏自身窗口。</summary>
    public bool HideSelfOnCapture { get; set; } = true;

    /// <summary>选中区域时显示放大镜。</summary>
    public bool ShowMagnifier { get; set; } = true;

    /// <summary>选区时显示标尺与尺寸。</summary>
    public bool ShowRuler { get; set; } = true;

    /// <summary>录制格式。</summary>
    public RecordFormat RecordFormat { get; set; } = RecordFormat.Gif;

    /// <summary>录制的帧率。</summary>
    public int RecordFps { get; set; } = 12;

    /// <summary>翻译目标语言。</summary>
    public string TranslateTarget { get; set; } = "zh-CN";

    /// <summary>翻译引擎。</summary>
    public TranslateProvider TranslateProvider { get; set; } = TranslateProvider.Builtin;

    /// <summary>硅基流动 API Key（用于 Hunyuan-MT-7B 在线翻译与 DeepSeek-OCR 在线识别）。</summary>
    public string SiliconFlowApiKey { get; set; } = "";

    /// <summary>硅基流动接口地址（OpenAI 兼容）。</summary>
    public string SiliconFlowBaseUrl { get; set; } = "https://api.siliconflow.cn/v1";

    /// <summary>硅基流动翻译模型（混元翻译大模型）。</summary>
    public string SiliconFlowTranslateModel { get; set; } = "tencent/Hunyuan-MT-7B";

    /// <summary>硅基流动在线识别模型（DeepSeek-OCR 专用 OCR 大模型）。</summary>
    public string SiliconFlowOcrModel { get; set; } = "deepseek-ai/DeepSeek-OCR";

    /// <summary>识图引擎（手动选择）。</summary>
    public OcrProvider OcrProvider { get; set; } = OcrProvider.DeepSeekOcr;

    /// <summary>
    /// 硅基流动接口地址白名单校验：只允许 HTTPS 且主机为官方域名。
    /// 自定义 baseUrl 若被篡改为攻击者端点，请求携带的 Authorization(Bearer APIKey)
    /// 会被外发，因此这里强制约束，防止凭证泄露。返回 null 表示使用默认值。
    /// </summary>
    public static string? ValidateSiliconFlowBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        var host = uri.Host;
        if (string.Equals(host, "api.siliconflow.cn", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "api.siliconflow.com", StringComparison.OrdinalIgnoreCase))
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return null;
    }

    public static AppSettings Load()
    {
        try
        {
            // 配置项零散缺失时，System.Text.Json 会用属性初始值填补，
            // 因此新增设置项无需迁移逻辑，直接反序列化即可。
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // 配置损坏时回退到默认值，不影响程序启动
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch
        {
            // 忽略写入失败（磁盘只读 / 权限不足）
        }
    }
}

public enum SaveBehavior
{
    CopyToClipboard,
    SaveToFile,
    CopyAndSave,
    PinToDesktop
}

public enum ImageFormatKind
{
    Png,
    Jpeg,
    Bmp
}

public enum RecordFormat
{
    Gif,
    Mp4
}

/// <summary>翻译引擎。</summary>
public enum TranslateProvider
{
    /// <summary>内置在线接口（免配置，逐行/批量走公共翻译端点）。</summary>
    Builtin,

    /// <summary>硅基流动（SiliconFlow）混元翻译大模型 Hunyuan-MT-7B。</summary>
    SiliconFlow
}

/// <summary>识图引擎。</summary>
public enum OcrProvider
{
    /// <summary>硅基流动在线 DeepSeek-OCR 专用 OCR 大模型。</summary>
    DeepSeekOcr,

    /// <summary>系统自带 WinRT OCR 引擎（免配置，翻译原位替换的精确坐标来源）。</summary>
    LocalWinRT
}
