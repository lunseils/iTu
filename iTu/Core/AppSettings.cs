using System.Text.Json;
using System.Text.Json.Serialization;

namespace iTu.Core;

public sealed class AppSettings
{
    private const string ProductName = "iTu";

    /// <summary>旧产品名，仅用于首次运行时迁移历史配置。</summary>
    private const string LegacyName = "iTu";

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName);

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        LegacyName, "settings.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>自由截图（拖拽框选）热键。</summary>
    public string CaptureHotkey { get; set; } = "Ctrl+Alt+A";

    /// <summary>全屏截图热键（不框选，直接抓整块虚拟桌面）。</summary>
    public string FullscreenHotkey { get; set; } = "Ctrl+Shift+F";

    /// <summary>屏幕录制热键。</summary>
    public string RecordHotkey { get; set; } = "Ctrl+Alt+S";

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

    /// <summary>
    /// 开源 AI 翻译的接口地址（OpenAI 兼容）。
    /// 默认指向本机 Ollama；也可填 LM Studio / vLLM / 自建网关。
    /// </summary>
    public string AiBaseUrl { get; set; } = "http://127.0.0.1:11434/v1";

    /// <summary>开源 AI 翻译使用的模型名。</summary>
    public string AiModel { get; set; } = "qwen2.5:7b";

    /// <summary>接口密钥（本地服务通常留空）。</summary>
    public string AiApiKey { get; set; } = "";

    public static AppSettings Load()
    {
        try
        {
            // 新路径优先；首次升级时从旧产品目录迁移，避免用户配置丢失
            var path = File.Exists(FilePath) ? FilePath
                     : File.Exists(LegacyFilePath) ? LegacyFilePath
                     : null;

            if (path != null)
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Opts);
                if (loaded != null)
                {
                    // 发现来自旧路径，立刻写到新位置，下次直接命中
                    if (path == LegacyFilePath) loaded.Save();
                    return loaded;
                }
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

    /// <summary>开源 AI 模型（OpenAI 兼容接口，如本地 Ollama / LM Studio）。</summary>
    OpenSourceAi
}
