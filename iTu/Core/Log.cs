namespace iTu.Core;

/// <summary>
/// 轻量日志：写入临时目录，便于排查启动与运行问题。
/// 任何写入失败都静默忽略，绝不影响主流程。
///
/// 日志是用户自证"我跑的到底是哪个版本、拖拽走到了哪一步"的唯一凭据，
/// 因此所有关键链路（启动/单实例/热键/浮层输入/翻译）都带上下文字段。
/// </summary>
public static class Log
{
    private static readonly string Path_ =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "itu.log");

    private static readonly object Gate = new();

    /// <summary>日志文件完整路径（托盘菜单里展示给用户）。</summary>
    public static string FilePath => Path_;

    /// <summary>日志超过该体积时滚动一次，避免长期运行无限增长。</summary>
    private const long MaxBytes = 512 * 1024;

    public static void Info(string message) => Append(message);

    /// <summary>写一条醒目的分隔线（每次启动写一条，便于截取某次运行的日志）。</summary>
    public static void Session(string title)
    {
        try
        {
            lock (Gate)
            {
                try
                {
                    var fi = new FileInfo(Path_);
                    if (fi.Exists && fi.Length > MaxBytes)
                        File.Move(Path_, Path_ + ".1", true);
                }
                catch
                {
                    // 滚动失败不影响写入
                }
                File.AppendAllText(Path_,
                    $"================ {title}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private static void Append(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path_,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
