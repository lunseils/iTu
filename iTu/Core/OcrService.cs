using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace iTu.Core;

/// <summary>识别出的一行文字及其在原图中的包围盒。</summary>
public sealed record OcrLine(string Text, Rectangle Bounds);

/// <summary>
/// 屏幕文字识别。
///
/// 两条后端（手动选择，见 <see cref="OcrProvider"/>）：
///   · DeepSeek-OCR —— 硅基流动在线专用 OCR 大模型（deepseek-ai/DeepSeek-OCR），
///                    grounding 模式返回逐块坐标；
///   · Local        —— Windows 自带 PowerShell 5.1 + WinRT OCR 引擎，免配置本地识别。
///
/// 默认走在线 DeepSeek-OCR；所选后端失败或无结果时回退本地 WinRT OCR。
/// 本地 WinRT 引擎无需 Windows SDK 投影，单文件发布后仅约 170KB。
///
/// 若系统未安装 OCR 语言包，会返回明确的错误提示而非静默失败。
/// </summary>
public static class OcrService
{
    /// <summary>
    /// 内嵌的 OCR 脚本文本（ocr.ps1 作为 EmbeddedResource 编译进 exe）。
    /// 发布目录因此只需一个 exe，不再有独立的 ocr.ps1 文件。
    /// </summary>
    private static readonly string EmbeddedScript = LoadEmbeddedScript();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static string LoadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("iTu.OcrScript")
            ?? throw new InvalidOperationException("OCR 脚本资源（iTu.OcrScript）未嵌入程序集");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static async Task<string> RecognizeAsync(Bitmap bitmap)
    {
        var output = await RunAsync(bitmap, lines: false);
        return output?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 识别并返回逐行结果（含包围盒），用于把译文回填到原位置。
    ///
    /// 按 <see cref="AppSettings.OcrProvider"/> 手动选择后端；所选后端失败或无结果时
    /// 统一回退本地 WinRT OCR。
    /// </summary>
    public static async Task<IReadOnlyList<OcrLine>> RecognizeLinesAsync(Bitmap bitmap, AppSettings settings)
    {
        var provider = settings.OcrProvider;

        if (provider == OcrProvider.DeepSeekOcr)
        {
            if (!string.IsNullOrWhiteSpace(settings.SiliconFlowApiKey))
            {
                try
                {
                    var lines = await RecognizeLinesWithDeepSeekOcrAsync(bitmap, settings);
                    if (lines.Count > 0) return lines;
                    Log.Info("[ocr] DeepSeek-OCR 无结果，回退本地 OCR");
                }
                catch (Exception ex)
                {
                    Log.Info("[ocr] DeepSeek-OCR 失败，回退本地 OCR: " + ex.Message);
                }
            }
            else
            {
                Log.Info("[ocr] 未配置硅基流动 API Key，无法在线识别，回退本地 OCR");
            }
        }

        return await RecognizeLinesLocalAsync(bitmap);
    }

    /// <summary>本地 WinRT OCR（逐行 + 包围盒）。</summary>
    public static async Task<IReadOnlyList<OcrLine>> RecognizeLinesLocalAsync(Bitmap bitmap)
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

    // ---------------- 硅基流动在线 DeepSeek-OCR ----------------

    /// <summary>
    /// 用硅基流动在线 DeepSeek-OCR 识别。纯文本模式（stable），返回逐行文字。
    /// 由于 DeepSeek-OCR 的 grounding 坐标输出不稳定（会重复/错乱），这里用纯文本模式，
    /// 每行用整幅图宽度兜底坐标；文字提取完全够用，翻译写回时定位到整图（精度略降）。
    /// </summary>
    private static async Task<IReadOnlyList<OcrLine>> RecognizeLinesWithDeepSeekOcrAsync(
        Bitmap bitmap, AppSettings settings)
    {
        // 端点必须过白名单：非法/被篡改的地址会让 Bearer APIKey 外发。
        var baseUrl = AppSettings.ValidateSiliconFlowBaseUrl(settings.SiliconFlowBaseUrl)
                      ?? "https://api.siliconflow.cn/v1";

        var imageData = EncodePng(bitmap);
        int imgW = bitmap.Width, imgH = bitmap.Height;

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.SiliconFlowOcrModel)
                ? "deepseek-ai/DeepSeek-OCR" : settings.SiliconFlowOcrModel,
            temperature = 0.0,
            max_tokens = 4096,
            stream = false,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = "data:image/png;base64," + imageData } },
                        new
                        {
                            type = "text",
                            text = "请提取图片中的所有文字，按行输出，不要添加任何解释。"
                        }
                    }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SiliconFlowApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"DeepSeek-OCR 接口返回 {(int)resp.StatusCode}: {Clip(err)}");
        }

        var body = await resp.Content.ReadAsStringAsync();
        string content;
        try
        {
            using var doc = JsonDocument.Parse(body);
            content = doc.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("DeepSeek-OCR 响应解析失败: " + ex.Message);
        }

        return ParsePlainLines(content, imgW, imgH);
    }

    /// <summary>
    /// 把纯文本逐行结果拆成 <see cref="OcrLine"/>（每行用整幅图宽度兜底坐标）。
    /// </summary>
    private static List<OcrLine> ParsePlainLines(string content, int imgW, int imgH)
    {
        var result = new List<OcrLine>();
        if (string.IsNullOrWhiteSpace(content) || imgW <= 0 || imgH <= 0) return result;

        foreach (var line in content.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            result.Add(new OcrLine(t, new Rectangle(0, 0, imgW, imgH)));
        }
        return result;
    }

    private static string EncodePng(Bitmap bitmap)
    {
        using var ms = new System.IO.MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static string Clip(string s)
    {
        var one = (s ?? "").Replace("\r", "").Replace("\n", " ");
        return one.Length <= 200 ? one : one[..200] + "…";
    }

    // ---------------- 本地 WinRT OCR 公共方法 ----------------

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
        // 脚本来自 exe 内嵌资源；这里只需把图片落到临时 PNG 供 WinRT 读取。
        var tempPath = Path.Combine(Path.GetTempPath(),
            "qqshot_ocr_" + Guid.NewGuid().ToString("N") + ".png");

        try
        {
            bitmap.Save(tempPath, ImageFormat.Png);
            return await RunPowerShellAsync(tempPath, lines);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 忽略 */ }
        }
    }

    private static async Task<string> RunPowerShellAsync(string imagePath, bool lines)
    {
        // 脚本通过 -EncodedCommand 直接传给 powershell.exe，不再落盘的 ocr.ps1。
        // 参数走环境变量：彻底避免把图片路径拼进命令行带来的引号/注入问题。
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(EmbeddedScript));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.EnvironmentVariables["ITU_OCR_PATH"] = imagePath;
        psi.EnvironmentVariables["ITU_OCR_LINES"] = lines ? "1" : "0";

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
            // BeginOutputReadLine/BeginErrorReadLine 是异步读取，Kill 后需再等一次
            // 无参 WaitForExit 让挂起的读取回调冲刷完，避免丢失进程退出前的输出。
            try { proc.WaitForExit(1000); } catch { /* 已退出 */ }
            throw new TimeoutException("文字识别超时（30 秒），已终止识别进程");
        }

        // 异步输出回调可能在 WaitForExitAsync 返回后仍未冲刷完，等一次同步退出收尾。
        // 注意：无参 WaitForExit 会等待异步事件处理完成，参数重载（含超时）不会。
        proc.WaitForExit();

        if (proc.ExitCode == 2)
            throw new InvalidOperationException("系统未安装可用的 OCR 语言包，请在「设置 → 语言和区域」中安装光学字符识别组件");
        if (proc.ExitCode != 0)
            throw new InvalidOperationException("文字识别失败：" + stderr.ToString().Trim());

        return stdout.ToString();
    }
}
