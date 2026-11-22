using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace iTu.Core;

/// <summary>
/// 屏幕文字翻译。
///
/// 两条链路：
///   · Builtin      —— 公共翻译端点（Google，失败回退 MyMemory），免配置；
///   · OpenSourceAi —— OpenAI 兼容接口，可接本地 Ollama / LM Studio / vLLM 等开源模型。
///
/// 速度优化（关键）：
///   1. 去重：同一批里重复的行只翻译一次；
///   2. 批量：所有唯一行合成**一次**请求往返（而不是每行一次）；
///   3. 校验：批量结果行数对不上时，自动退回逐行 + 有界并发，保证正确性。
/// </summary>
public static class TranslateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>单次请求最多携带的行数，避免请求体过大或模型输出超长。</summary>
    private const int MaxLinesPerRequest = 40;

    /// <summary>逐行回退时的并发度。</summary>
    private const int FallbackConcurrency = 6;

    /// <summary>
    /// 批量翻译若干行，返回与入参**等长**的数组（空行对应空串）。
    /// </summary>
    public static async Task<string[]> TranslateLinesAsync(
        IReadOnlyList<string> lines, AppSettings settings, CancellationToken ct = default)
    {
        var result = new string[lines.Count];
        if (lines.Count == 0) return result;

        // ---- 1) 去重 ----
        var unique = new List<string>();
        var indexMap = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i]?.Trim() ?? "";
            if (t.Length == 0) continue;

            if (!indexMap.TryGetValue(t, out var list))
            {
                list = new List<int>();
                indexMap[t] = list;
                unique.Add(t);
            }
            list.Add(i);
        }
        if (unique.Count == 0) return result;

        // ---- 2) 分批翻译 ----
        var translated = new string[unique.Count];
        for (int start = 0; start < unique.Count; start += MaxLinesPerRequest)
        {
            int count = Math.Min(MaxLinesPerRequest, unique.Count - start);
            var slice = unique.GetRange(start, count);

            string[]? part = settings.TranslateProvider == TranslateProvider.OpenSourceAi
                ? await TryTranslateWithAiAsync(slice, settings, ct)
                : await TryTranslateBuiltinBatchAsync(slice, settings.TranslateTarget, ct);

            // ---- 3) 批量失败则逐行回退 ----
            part ??= await TranslatePerLineAsync(slice, settings, ct);

            Array.Copy(part, 0, translated, start, count);
        }

        // ---- 4) 回填 ----
        for (int u = 0; u < unique.Count; u++)
        {
            foreach (var idx in indexMap[unique[u]])
                result[idx] = translated[u];
        }
        return result;
    }

    /// <summary>单行翻译（保留给不需要位置信息的调用方）。</summary>
    public static async Task<string> TranslateAsync(string text, string targetLang = "zh-CN")
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return await TranslateWithFallbackAsync(text, targetLang, CancellationToken.None);
    }

    // ---------------- 开源 AI（OpenAI 兼容） ----------------

    private static async Task<string[]?> TryTranslateWithAiAsync(
        List<string> lines, AppSettings s, CancellationToken ct)
    {
        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(s.AiBaseUrl)
                ? "http://127.0.0.1:11434/v1"
                : s.AiBaseUrl.TrimEnd('/');

            var lang = DescribeLanguage(s.TranslateTarget);

            var prompt = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
                prompt.Append(i + 1).Append(". ").Append(lines[i]).Append('\n');

            var payload = new
            {
                model = string.IsNullOrWhiteSpace(s.AiModel) ? "qwen2.5:7b" : s.AiModel,
                temperature = 0.2,
                stream = false,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content =
                            $"你是翻译引擎。把用户给出的每一条编号行翻译成{lang}。" +
                            $"必须输出且仅输出 {lines.Count} 行，每行保持原有编号和 '. ' 前缀，" +
                            "不要解释、不要合并、不要增删条目。"
                    },
                    new { role = "user", content = prompt.ToString() }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
            if (!string.IsNullOrWhiteSpace(s.AiApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AiApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Info($"[translate] AI 接口返回 {(int)resp.StatusCode}");
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            var parsed = ParseNumbered(content, lines.Count);
            if (parsed == null)
                Log.Info("[translate] AI 输出行数不匹配，回退逐行翻译");

            return parsed;
        }
        catch (Exception ex)
        {
            Log.Info("[translate] AI 翻译失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>解析 "1. 译文" 形式的输出；行数不符返回 null。</summary>
    private static string[]? ParseNumbered(string content, int expected)
    {
        var map = new Dictionary<int, string>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            int i = 0;
            while (i < line.Length && char.IsDigit(line[i])) i++;
            if (i == 0) continue;

            if (!int.TryParse(line[..i], out var n)) continue;
            if (n < 1 || n > expected) continue;

            var rest = line[i..].TrimStart('.', '。', '、', ')', '．', ' ');
            if (rest.Length == 0) continue;

            map[n] = rest;
        }

        if (map.Count != expected) return null;

        var arr = new string[expected];
        for (int k = 0; k < expected; k++)
        {
            if (!map.TryGetValue(k + 1, out var v)) return null;
            arr[k] = v;
        }
        return arr;
    }

    private static string DescribeLanguage(string tag) => tag switch
    {
        "zh-CN" or "zh" => "简体中文",
        "zh-TW" => "繁体中文",
        "en" => "英语",
        "ja" => "日语",
        "ko" => "韩语",
        "fr" => "法语",
        "de" => "德语",
        "ru" => "俄语",
        "es" => "西班牙语",
        _ => "简体中文"
    };

    // ---------------- 内置在线接口 ----------------

    /// <summary>
    /// 内置引擎的批量翻译。
    ///
    /// 依次尝试：
    ///   1. MyMemory 合并批量 —— 实测国内可直连、单次 1~2 秒，
    ///      且换行会被完整保留，一次请求即可拿回整批译文（按字节上限分块）；
    ///   2. Google 合并 —— 部分地区 Google 更快，作为第二选择；
    ///   3. 都失败 → 返回 null，由调用方走逐行并发回退。
    ///
    /// 注：曾尝试 Edge 翻译接口，其 auth 端点已下线（返回 404），故移除。
    /// </summary>
    private static async Task<string[]?> TryTranslateBuiltinBatchAsync(
        List<string> lines, string target, CancellationToken ct)
    {
        // 1) MyMemory 合并批量（首选：快且换行稳定）
        var mm = await TryTranslateMyMemoryBatchAsync(lines, target, ct);
        if (mm != null) return mm;

        // 2) Google 合并批量
        try
        {
            var joined = string.Join("\n", lines);
            var translated = await TranslateGoogleAsync(joined, target, ct);
            var parts = translated.Split('\n');

            // 行数必须对得上，否则无法确定译文归属
            if (parts.Length != lines.Count)
            {
                Log.Info($"[translate] Google 批量行数不符 {parts.Length}≠{lines.Count}");
                return null;
            }

            for (int i = 0; i < parts.Length; i++)
                parts[i] = parts[i].Trim();

            return parts;
        }
        catch (Exception ex)
        {
            Log.Info("[translate] Google 批量失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>MyMemory 限流时的一次重试间隔：给得太短会继续被拒，太长又让用户干等。</summary>
    private const int RetryDelayMs = 450;

    /// <summary>日志里截断长文本，避免一行日志刷屏。</summary>
    private static string Clip(string s)
    {
        var one = (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
        return one.Length <= 120 ? one : one[..120] + "…";
    }

    /// <summary>这一批的译文是否与原文逐行相同（= 接口把原文回显了）。</summary>
    private static bool IsEcho(List<string> source, string[] translated, int offset)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (!string.Equals(source[i].Trim(), (translated[offset + i] ?? "").Trim(),
                                StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>MyMemory 单次请求有长度上限，按 UTF-8 字节数分块后逐块合并翻译。</summary>
    private static async Task<string[]?> TryTranslateMyMemoryBatchAsync(
        List<string> lines, string target, CancellationToken ct)
    {
        const int MaxBytesPerRequest = 420;

        var result = new string[lines.Count];
        int start = 0;

        while (start < lines.Count)
        {
            int end = start;
            int bytes = 0;
            while (end < lines.Count)
            {
                int add = Encoding.UTF8.GetByteCount(lines[end]) + 1;
                if (end > start && bytes + add > MaxBytesPerRequest) break;
                bytes += add;
                end++;
            }

            var chunk = lines.GetRange(start, end - start);
            string translated;
            try
            {
                translated = await TranslateMyMemoryAsync(string.Join("\n", chunk), target, ct);
            }
            catch (Exception ex)
            {
                Log.Info("[translate] MyMemory 批量失败: " + ex.Message);
                return null;
            }

            var parts = translated.Split('\n');
            if (parts.Length != chunk.Count)
            {
                Log.Info($"[translate] MyMemory 批量行数不符 {parts.Length}≠{chunk.Count}");
                return null;
            }

            for (int i = 0; i < parts.Length; i++)
                result[start + i] = parts[i].Trim();

            // MyMemory 在限流 / 超免费配额时会把原文**原样回显**：不报错、行数也对得上，
            // 表现就是"点了翻译没反应"（上层判定为"译文与原文完全一致"）。
            // 这种可疑结果隔一会儿重试一次，仍不行就交给 Google / 逐行回退。
            if (IsEcho(chunk, result, start))
            {
                Log.Info($"[translate] MyMemory 回显原文（疑似限流），{RetryDelayMs}ms 后重试一次 " +
                         $"src=\"{Clip(string.Join("\n", chunk))}\" resp=\"{Clip(translated)}\"");
                await Task.Delay(RetryDelayMs, ct);

                try
                {
                    translated = await TranslateMyMemoryAsync(string.Join("\n", chunk), target, ct);
                }
                catch (Exception ex)
                {
                    Log.Info("[translate] MyMemory 重试失败: " + ex.Message);
                    return null;
                }

                var again = translated.Split('\n');
                if (again.Length != chunk.Count)
                {
                    Log.Info($"[translate] MyMemory 重试行数不符 {again.Length}≠{chunk.Count}");
                    return null;
                }

                for (int i = 0; i < again.Length; i++)
                    result[start + i] = again[i].Trim();

                if (IsEcho(chunk, result, start))
                    Log.Info("[translate] MyMemory 重试后仍回显原文");
            }

            start = end;
        }

        return result;
    }

    private static async Task<string> TranslateWithFallbackAsync(string text, string target, CancellationToken ct)
    {
        // MyMemory 优先：国内可直连，单句约 1 秒（Google 在此环境需 10 秒以上）
        try
        {
            var r = await TranslateMyMemoryAsync(text, target, ct);
            if (!string.IsNullOrWhiteSpace(r)) return r;
        }
        catch (Exception ex)
        {
            Log.Info("[translate] MyMemory 单句失败: " + ex.Message);
        }

        return await TranslateGoogleAsync(text, target, ct);
    }

    /// <summary>逐行并发翻译（批量不可用时的回退路径）。</summary>
    private static async Task<string[]> TranslatePerLineAsync(
        List<string> lines, AppSettings settings, CancellationToken ct)
    {
        var arr = new string[lines.Count];

        // AI 模式逐行会非常慢，并发度更保守
        int concurrency = settings.TranslateProvider == TranslateProvider.OpenSourceAi ? 2 : FallbackConcurrency;

        using var gate = new SemaphoreSlim(concurrency);
        var tasks = lines.Select(async (text, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                arr[i] = settings.TranslateProvider == TranslateProvider.OpenSourceAi
                    ? (await TryTranslateWithAiAsync(new List<string> { text }, settings, ct))?[0] ?? text
                    : await TranslateWithFallbackAsync(text, settings.TranslateTarget, ct);
            }
            catch
            {
                arr[i] = text;
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        return arr;
    }

    // ---------------- 公共端点实现 ----------------

    private static async Task<string> TranslateGoogleAsync(string text, string target, CancellationToken ct = default)
    {
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(target)}&dt=t&q={Uri.EscapeDataString(text)}";
        var body = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var chunk in root[0].EnumerateArray())
        {
            if (chunk.ValueKind == JsonValueKind.Array && chunk.GetArrayLength() > 0)
                sb.Append(chunk[0].GetString());
        }
        var result = sb.ToString();
        if (!string.IsNullOrWhiteSpace(result)) return result;
        throw new InvalidOperationException("翻译接口返回空结果");
    }

    private static async Task<string> TranslateMyMemoryAsync(string text, string target, CancellationToken ct = default)
    {
        var source = target.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "en" : "auto";
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={source}|{target}";
        var body = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("responseData", out var rd) &&
            rd.TryGetProperty("translatedText", out var tt))
        {
            var result = tt.GetString() ?? "";
            return System.Net.WebUtility.HtmlDecode(result);
        }
        throw new InvalidOperationException("翻译接口返回异常");
    }
}
