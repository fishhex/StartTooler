using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using StartTooler.Data;

namespace StartTooler.Services;

/// <summary>
/// 单次 AI 打标成功结果。
/// </summary>
public sealed record TagResult(
    IReadOnlyList<string> Tags,
    int Score,
    int LatencyMs,
    string Model);

/// <summary>
/// 单次 AI 打标失败。IsFatal=true 触发整批终止（如 HTTP 401 Key 失效）；false 仅跳过单文件。
/// </summary>
public sealed record TagFailure(
    string Reason,
    bool IsFatal);

/// <summary>
/// v0.6 AI 打标服务接口（图片 + 视频；D.1 只实现图片，视频走 NotImplemented 待 D.2）。
///
/// **v0.6 设计原则**：运行时只读 AIConfig，不读 TOML。协议分发按 aiCfg.Protocol 字段。
/// </summary>
public interface IAITagger
{
    /// <summary>
    /// 对单文件路由：图片走图片 prompt；视频（D.2 才做）走 ffmpeg 抽帧 + 视频 prompt。
    /// </summary>
    Task<(TagResult? Result, TagFailure? Failure)> TagFileAsync(
        MediaFile file,
        AIConfig config,
        CancellationToken ct);
}

/// <summary>
/// AI 视觉打标实现。支持 OpenAI 兼容 / Anthropic 协议（按 aiCfg.Protocol 路由）。
///
/// 串行调用约定：本类不做节流，节流由调用方（GalleryViewModel.BatchTag）控制 200ms 间隔。
/// 重试约定：本类对单次调用做 1 次 JSON retry + HTTP 状态码重试（429 / 5xx），其它错误直接返回。
/// </summary>
public sealed class AITagger : IAITagger
{
    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        // SocketsHttpHandler 池化连接，避免每次 new HttpClient 触发 TIME_WAIT 累积。
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 10,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            // 单次 AI 调用整体超时（含重试）；单次请求超时由 CancellationTokenSource 控制。
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public async Task<(TagResult? Result, TagFailure? Failure)> TagFileAsync(
        MediaFile file,
        AIConfig config,
        CancellationToken ct)
    {
        // D.1 仅实现图片；视频 D.2 加 ffmpeg 抽帧
        if (file.MediaType != MediaType.Image)
        {
            return (null, new TagFailure($"暂不支持 {file.MediaType} 类型打标（D.2 视频抽帧待实施）", IsFatal: false));
        }

        var localPath = Path.Combine(file.ProjectPath, file.RelativePath);
        if (!File.Exists(localPath))
        {
            return (null, new TagFailure($"本地文件不存在：{file.FileName}", IsFatal: false));
        }

        // 输入校验
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            return (null, new TagFailure("API Key 为空", IsFatal: false));
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            return (null, new TagFailure("Base URL 为空", IsFatal: false));
        if (string.IsNullOrWhiteSpace(config.Model))
            return (null, new TagFailure("Model 为空", IsFatal: false));
        // Protocol 老 config.db 兼容：null/空串 fallback 到 "OpenAI"
        var protocol = string.IsNullOrWhiteSpace(config.Protocol) ? "OpenAI" : config.Protocol;

        // 1. 图片 resize → JPEG bytes
        byte[] imageBytes;
        try
        {
            imageBytes = ResizeToJpegBytes(localPath, maxEdge: 512, quality: 75);
        }
        catch (Exception ex)
        {
            return (null, new TagFailure($"图片处理失败：{ex.Message}", IsFatal: false));
        }

        // 2. base64
        var b64 = Convert.ToBase64String(imageBytes);

        // 3. prompt（含白名单拼入）
        var prompt = BuildImagePrompt();

        // debug: 打印 prompt（排查"响应未提取到文本"等解析问题用）
        Trace.WriteLine($"[AITagger] Prompt → model={config.Model}, protocol={protocol}, imageBytes={imageBytes.Length}, promptLen={prompt.Length}");
        Trace.WriteLine($"--- PROMPT START ---\n{prompt}\n--- PROMPT END ---");

        // 4. 发请求 + 重试 + 解析
        return await CallAndParseAsync(config, protocol, prompt, new[] { b64 }, ct);
    }

    /// <summary>
    /// SkiaSharp 长边 resize 到 maxEdge，JPEG q=quality。返回 jpg bytes。
    /// 失败抛异常（让调用方决定怎么处理）。
    /// </summary>
    private static byte[] ResizeToJpegBytes(string localPath, int maxEdge, int quality)
    {
        using var input = File.OpenRead(localPath);
        using var bitmap = SKBitmap.Decode(input);
        if (bitmap == null) throw new InvalidDataException($"无法解码图片：{Path.GetFileName(localPath)}");

        var (newW, newH) = ComputeFitSize(bitmap.Width, bitmap.Height, maxEdge);
        SKImage image;
        if (newW == bitmap.Width && newH == bitmap.Height)
        {
            // 不需要 resize，直接从原 bitmap 取 image（同一份内存，bitmap dispose 后仍有效）
            image = SKImage.FromBitmap(bitmap);
        }
        else
        {
            var resized = bitmap.Resize(new SKImageInfo(newW, newH), SKFilterQuality.Medium);
            if (resized == null) throw new InvalidDataException($"resize 失败：{bitmap.Width}x{bitmap.Height} → {newW}x{newH}");
            image = SKImage.FromBitmap(resized);
            resized.Dispose();
        }

        try
        {
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        }
        finally
        {
            image.Dispose();
        }
    }

    private static (int Width, int Height) ComputeFitSize(int w, int h, int maxEdge)
    {
        if (w <= maxEdge && h <= maxEdge) return (w, h);
        if (w >= h)
        {
            var newW = maxEdge;
            var newH = (int)Math.Round((double)h * maxEdge / w);
            return (newW, newH);
        }
        else
        {
            var newH = maxEdge;
            var newW = (int)Math.Round((double)w * maxEdge / h);
            return (newW, newH);
        }
    }

    /// <summary>
    /// 发请求 + 解析 + 重试。对单次调用做：
    ///   - HTTP 401/403：IsFatal=true（Key 失效）
    ///   - HTTP 429：backoff 3s/6s/12s × 3
    ///   - HTTP 5xx：backoff 1s/3s × 2
    ///   - JSON 解析失败：retry 1 次（不变 prompt，靠模型再生成）
    ///   - HTTP 4xx（其它）/ 超时：单图跳过
    /// </summary>
    private async Task<(TagResult? Result, TagFailure? Failure)> CallAndParseAsync(
        AIConfig config,
        string protocol,
        string prompt,
        IReadOnlyList<string> imagesB64,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // 重试循环
        int[] backoffMs5xx = { 1000, 3000 };
        int[] backoffMs429 = { 3000, 6000, 12000 };
        int retry5xxIdx = 0;
        int retry429Idx = 0;
        int jsonRetryLeft = 1;

        while (true)
        {
            // 单次请求 timeout 30s（用户取消时 ct 优先触发）
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(TimeSpan.FromSeconds(30));

            HttpResponseMessage? resp = null;
            string? respBody = null;
            try
            {
                resp = protocol switch
                {
                    "OpenAI"    => await SendOpenAIAsync(config, prompt, imagesB64, reqCts.Token),
                    "Anthropic" => await SendAnthropicAsync(config, prompt, imagesB64, reqCts.Token),
                    _ => throw new NotSupportedException($"未知 Protocol：{protocol}（AIConfig.Protocol 必须是 Anthropic / OpenAI）"),
                };

                // 401 / 403: 立刻终止（Key 失效）
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var body = await resp.Content.ReadAsStringAsync(reqCts.Token);
                    return (null, new TagFailure(
                        $"HTTP {(int)resp.StatusCode}：{Truncate(body, 150)}（请检查 API Key）",
                        IsFatal: true));
                }

                // 429: backoff 重试
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (retry429Idx < backoffMs429.Length)
                    {
                        var wait = backoffMs429[retry429Idx++];
                        Trace.WriteLine($"[AITagger] 429 rate limit, retry in {wait}ms");
                        await Task.Delay(wait, ct);
                        continue;
                    }
                    var body = await resp.Content.ReadAsStringAsync(reqCts.Token);
                    return (null, new TagFailure($"rate limit（重试 3 次仍失败）：{Truncate(body, 100)}", IsFatal: false));
                }

                // 5xx: backoff 重试
                if ((int)resp.StatusCode >= 500)
                {
                    if (retry5xxIdx < backoffMs5xx.Length)
                    {
                        var wait = backoffMs5xx[retry5xxIdx++];
                        Trace.WriteLine($"[AITagger] {(int)resp.StatusCode} server error, retry in {wait}ms");
                        await Task.Delay(wait, ct);
                        continue;
                    }
                    var body = await resp.Content.ReadAsStringAsync(reqCts.Token);
                    return (null, new TagFailure($"HTTP {(int)resp.StatusCode}：{Truncate(body, 100)}", IsFatal: false));
                }

                // 4xx (除 401/403/429)
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(reqCts.Token);
                    return (null, new TagFailure($"HTTP {(int)resp.StatusCode}：{Truncate(body, 150)}", IsFatal: false));
                }

                respBody = await resp.Content.ReadAsStringAsync(reqCts.Token);

                // debug: 打印 response body（排查 schema 不匹配 / tool_calls-only 等"未提取到文本"问题）
                Trace.WriteLine($"[AITagger] Response ← status={(int)resp.StatusCode}, bodyLen={respBody?.Length ?? 0}, protocol={protocol}");
                Trace.WriteLine($"--- RESPONSE START ---\n{respBody}\n--- RESPONSE END ---");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户取消
                throw;
            }
            catch (OperationCanceledException)
            {
                // 单次请求 timeout（30s）
                if (jsonRetryLeft > 0)
                {
                    jsonRetryLeft--;
                    Trace.WriteLine("[AITagger] 单次请求 timeout，重试 1 次");
                    continue;
                }
                return (null, new TagFailure("请求超时", IsFatal: false));
            }
            catch (HttpRequestException ex)
            {
                if (jsonRetryLeft > 0)
                {
                    jsonRetryLeft--;
                    Trace.WriteLine($"[AITagger] 网络错误，重试 1 次：{ex.Message}");
                    continue;
                }
                return (null, new TagFailure($"网络错误：{Truncate(ex.Message, 150)}", IsFatal: false));
            }
            finally
            {
                resp?.Dispose();
            }

            // 成功：解析响应 → 提取文本 → ParseAndValidate
            var text = ExtractContentText(respBody, protocol);
            if (text == null)
            {
                if (jsonRetryLeft > 0)
                {
                    jsonRetryLeft--;
                    Trace.WriteLine("[AITagger] 响应未提取到文本，重试 1 次");
                    continue;
                }
                return (null, new TagFailure("响应格式异常：未找到文本字段", IsFatal: false));
            }

            var (parsed, parseErr) = ParseAndValidate(text);
            if (parseErr != null)
            {
                if (jsonRetryLeft > 0)
                {
                    jsonRetryLeft--;
                    Trace.WriteLine($"[AITagger] JSON 解析失败，重试 1 次：{parseErr}");
                    continue;
                }
                return (null, new TagFailure(parseErr, IsFatal: false));
            }

            sw.Stop();
            return (new TagResult(parsed!.Tags, parsed.Score, (int)sw.ElapsedMilliseconds, config.Model), null);
        }
    }

    private async Task<HttpResponseMessage> SendOpenAIAsync(
        AIConfig config, string prompt, IReadOnlyList<string> imagesB64, CancellationToken ct)
    {
        var url = config.BaseUrl.TrimEnd('/') + "/chat/completions";

        // content: [text, image_url × N]
        var contentParts = new List<object>
        {
            new { type = "text", text = prompt },
        };
        foreach (var b64 in imagesB64)
        {
            contentParts.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:image/jpeg;base64,{b64}" }
            });
        }

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        req.Content = JsonContent.Create(new
        {
            model = config.Model,
            max_tokens = 300,
            messages = new[] { new { role = "user", content = contentParts.ToArray() } }
        });

        return await _http.SendAsync(req, ct);
    }

    private async Task<HttpResponseMessage> SendAnthropicAsync(
        AIConfig config, string prompt, IReadOnlyList<string> imagesB64, CancellationToken ct)
    {
        var url = config.BaseUrl.TrimEnd('/') + "/v1/messages";

        // content: [text, image × N]
        var contentParts = new List<object>
        {
            new { type = "text", text = prompt },
        };
        foreach (var b64 in imagesB64)
        {
            contentParts.Add(new
            {
                type = "image",
                source = new { type = "base64", media_type = "image/jpeg", data = b64 }
            });
        }

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-api-key", config.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = JsonContent.Create(new
        {
            model = config.Model,
            max_tokens = 300,
            messages = new[] { new { role = "user", content = contentParts.ToArray() } }
        });

        return await _http.SendAsync(req, ct);
    }

    /// <summary>
    /// 从响应 body 提取文本内容。OpenAI: choices[0].message.content；Anthropic: content[0].text。
    /// </summary>
    private static string? ExtractContentText(string? respBody, string protocol)
    {
        if (string.IsNullOrWhiteSpace(respBody)) return null;
        try
        {
            using var doc = JsonDocument.Parse(respBody);
            var root = doc.RootElement;

            if (protocol == "OpenAI")
            {
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    return null;
                var msg = choices[0].GetProperty("message");
                return msg.GetProperty("content").GetString();
            }
            else if (protocol == "Anthropic")
            {
                if (!root.TryGetProperty("content", out var content) || content.GetArrayLength() == 0)
                    return null;
                return content[0].GetProperty("text").GetString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 AI 返回的 JSON 文本。剥 markdown 包裹 + 白名单过滤 + score clamp。
    /// 成功返回 (TagResult?, null)，失败返回 (null, error)。
    /// </summary>
    private static (TagResult? Result, string? Error) ParseAndValidate(string raw)
    {
        // 1. 剥 markdown 包裹（AI 偶尔返回 ```json ... ```）
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            raw = Regex.Replace(raw, @"^```(?:json)?\s*|\s*```$", "", RegexOptions.Multiline).Trim();
        }

        // 2. JSON parse
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return (null, $"JSON 解析失败：{ex.Message}");
        }

        // 3. 提取 tags
        var rawTags = new List<string>();
        if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tagEl in tagsEl.EnumerateArray())
            {
                var tag = tagEl.GetString()?.Trim();
                if (!string.IsNullOrEmpty(tag)) rawTags.Add(tag);
            }
        }

        // 4. 白名单过滤
        var validTags = rawTags
            .Where(t => ChineseTagVocabulary.Contains(t))
            .Distinct()
            .Take(7)
            .ToList();

        if (validTags.Count == 0)
        {
            validTags.Add("未分类");
            Trace.WriteLine($"[AITagger] all tags out of whitelist, fallback to '未分类'. raw=[{string.Join(",", rawTags)}]");
        }
        else if (validTags.Count < rawTags.Count)
        {
            var dropped = rawTags.Except(validTags).ToList();
            Trace.WriteLine($"[AITagger] filtered {dropped.Count} out-of-whitelist tags. dropped=[{string.Join(",", dropped)}]");
        }

        // 5. 提取 score + clamp
        int score = 0;
        if (root.TryGetProperty("score", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number)
        {
            score = scoreEl.TryGetInt32(out var s) ? Math.Clamp(s, 0, 100) : 0;
        }

        return (new TagResult(validTags, score, 0, ""), null);
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    /// <summary>
    /// 中文标签白名单（56 个）。调整时同步更新 doc/11-ai-tagging.md 附录 A + prompt。
    /// </summary>
    public static readonly IReadOnlyList<string> ChineseTagVocabulary = new[]
    {
        // 深空天体 (5)
        "星云", "星系", "星团", "超新星遗迹", "暗星云",

        // 太阳系 (8)
        "行星", "月亮", "太阳", "彗星", "小行星", "流星", "卫星", "国际空间站",

        // 命名天体 (15)
        "猎户座大星云", "M42", "仙女座星系", "M31", "昴星团", "M45",
        "银河", "银心", "土星", "木星", "火星", "金星",
        "娥眉月", "满月", "月面特写",

        // 现象 (6)
        "极光", "日食", "月食", "凌日", "月掩星", "合相",

        // 构图 / 拍摄手法 (10)
        "广角", "窄带", "行星摄影", "深空摄影", "星座", "长焦",
        "赤道仪跟踪", "行星叠加", "月面拼接", "全景",

        // 质量问题 (10)
        "拖线", "失焦", "噪点", "过曝", "欠曝", "色差",
        "大气抖动", "视宁度差", "镜头眩光", "杂光",

        // 特殊 (2)
        "非天文", "未分类",
    };

    /// <summary>
    /// 中文图片 prompt（含白名单拼入）。视频 prompt D.2 加。
    /// </summary>
    internal static string BuildImagePrompt()
    {
        var vocab = string.Join("、", ChineseTagVocabulary.Where(t => t != "非天文" && t != "未分类"));
        return $$"""
你是一位天文摄影专家，正在分析一张天文照片。

【标签白名单】（只允许使用下列词汇）：
{{vocab}}、非天文、未分类

【输出规则】
- 选 3-7 个最相关的标签（按相关性排序，最相关的在前）
- 评分 0-100 整数：
  · 90-100 作品级（可投稿 / 印刷出版）
  · 75-89  优秀（轻微瑕疵）
  · 60-74  良好（有可见问题但仍可用）
  · 40-59  一般（明显缺陷）
  · 0-39   较差（建议丢弃）
- 非天文内容（普通风景、测试图、色卡）→ tags:["非天文"]，score:0

【示例】（仅供格式参考，不要照抄内容）：
图：猎户座大星云 HOO 合成作品
输出：{"tags": ["星云", "猎户座大星云", "广角"], "score": 82}

---

现在分析这张图，按规则输出。
只输出 JSON，禁止 markdown / 解释文字：
{"tags": ["标签1", "标签2"], "score": 分数}
""";
    }
}