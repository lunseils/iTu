using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;

namespace QqShot.Core;

/// <summary>识别出的一行文字及其在原图中的包围盒。</summary>
public sealed record OcrLine(string Text, Rectangle Bounds);

/// <summary>
/// 屏幕文字识别。
///
/// 后端为 Windows 自带的 PowerShell 5.1 + WinRT OCR 引擎，
/// 因此主程序无需引用体积达 25MB 的 Windows SDK 投影，
/// 单文件发布后仅约 170KB。
///
/// 若系统未安装 OCR 语言包，会返回明确的错误提示而非静默失败。
/// </summary>
public static class OcrService
{
    private static readonly string ScriptPath = ResolveScriptPath();

    private static string ResolveScriptPath()
    {
        // 优先使用程序集同目录下的 ocr.ps1（发布时会一并复制）
        var baseDir = System.AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "ocr.ps1");
        if (File.Exists(candidate)) return candidate;

        // 开发态：源码目录
        var devPath = Path.Combine(baseDir, "..", "..", "..", "Core", "ocr.ps1");
        if (File.Exists(devPath)) return Path.GetFullPath(devPath);

        return candidate;
    }

    public static async Task<string> RecognizeAsync(Bitmap bitmap)
    {
        var output = await RunAsync(bitmap, lines: false);
        return output?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 识别并返回逐行结果（含包围盒），用于把译文回填到原位置。
    /// </summary>
    public static async Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(Bitmap bitmap)
    {
        var json = await RunAsync(bitmap, lines: true);
        var result = new List<OcrLine>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return result;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var text = item.TryGetProperty("t", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;

                int x = GetInt(item, "x"), y = GetInt(item, "y");
                int w = GetInt(item, "w"), h = GetInt(item, "h");
                if (w <= 0 || h <= 0) continue;

                result.Add(new OcrLine(NormalizeCjkSpacing(text!.Trim()), new Rectangle(x, y, w, h)));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 脚本异常输出时不让整条链路失败
            return result;
        }

        return result;
    }

    private static int GetInt(System.Text.Json.JsonElement item, string name)
        => item.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>
    /// WinRT OCR 对中文会按字切分并插入空格（"截 编 辑 器"），
    /// 这里去掉汉字之间的空格，恢复成正常词组，提升译文质量。
    /// </summary>
    private static string NormalizeCjkSpacing(string text)
    {
        if (text.Length < 3) return text;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ' && sb.Length > 0 && i + 1 < text.Length &&
                IsCjk(sb[^1]) && IsCjk(text[i + 1]))
            {
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||   // 基本汉字
        (c >= 0x3400 && c <= 0x4DBF) ||   // 扩展 A
        (c >= 0x3000 && c <= 0x303F) ||   // CJK 标点
        (c >= 0xFF00 && c <= 0xFFEF);     // 全角符号

    private static async Task<string> RunAsync(Bitmap bitmap, bool lines)
    {
        if (!File.Exists(ScriptPath))
            throw new InvalidOperationException("缺少 OCR 脚本（ocr.ps1）");

        // 落到临时 PNG 文件供脚本读取
        var tempPath = Path.Combine(Path.GetTempPath(),
            "qqshot_ocr_" + Guid.NewGuid().ToString("N") + ".png");

        try
        {
            bitmap.Save(tempPath, ImageFormat.Png);
            return await RunPowerShellAsync(ScriptPath, tempPath, lines);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略 */ }
        }
    }

    private static async Task<string> RunPowerShellAsync(string script, string imagePath, bool lines)
    {
        var extra = lines ? " -Lines" : "";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Path \"{imagePath}\"{extra}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var gate = new object();

        // 异步输出回调跑在线程池上，两个流可能同时回调
        proc.OutputDataReceived += (_, e) =>
        { if (e.Data != null) lock (gate) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) =>
        { if (e.Data != null) lock (gate) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 没有超时的 OCR 会永久占用位图和面板（旧实现的潜在风险）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw new TimeoutException("文字识别超时（30 秒），已终止识别进程");
        }

        if (proc.ExitCode == 2)
            throw new InvalidOperationException("系统未安装可用的 OCR 语言包，请在「设置 → 语言和区域」中安装光学字符识别组件");
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("文字识别失败：" + stderr.ToString().Trim());

        return stdout.ToString();
    }
}
