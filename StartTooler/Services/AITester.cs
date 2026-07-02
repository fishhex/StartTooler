using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Services;

/// <summary>
/// AI 配置连通性测试。发一个极小请求（max_tokens=16，prompt="say 'ok'"）验证：
///   - BaseUrl 可达
///   - 鉴权通过
///   - Model 存在
///
/// 请求协议由 <see cref="AIProviderMeta.ProtocolKind"/> 决定：
///   - Anthropic：POST {base}/v1/messages，header x-api-key + anthropic-version
///   - OpenAI  ：POST {base}/chat/completions，header Authorization: Bearer
///
/// Custom 默认走 OpenAI 兼容；测试结果附"协议假定"标注。
/// </summary>
public static class AITester
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private const string AnthropicVersion = "2023-06-01";

    public sealed record TestResult(
        bool Success,
        string Message,
        int LatencyMs,
        string? ProtocolNote = null);

    public static async Task<TestResult> TestAsync(
        AIProviderMeta meta,
        string apiKey,
        string baseUrl,
        string model,
        CancellationToken ct = default)
    {
        // 输入校验（本地直接 fail，避免发无意义的请求）
        if (meta.Provider == AIProvider.Custom && meta.ProtocolKind == ProtocolKind.OpenAI)
        {
            // 继续，但 message 加 note
        }
        if (string.IsNullOrWhiteSpace(apiKey))
            return new TestResult(false, "API Key 为空，请先填写", 0);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new TestResult(false, "Base URL 为空，请先填写", 0);
        if (string.IsNullOrWhiteSpace(model))
            return new TestResult(false, "Model 为空，请先填写", 0);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = meta.ProtocolKind switch
            {
                ProtocolKind.Anthropic => await SendAnthropicAsync(baseUrl, apiKey, model, ct),
                ProtocolKind.OpenAI => await SendOpenAIAsync(baseUrl, apiKey, model, ct),
                _ => new TestResult(false, $"未知协议：{meta.ProtocolKind}", (int)sw.ElapsedMilliseconds),
            };

            // Custom 用 OpenAI 协议时附一条 note（loader 已经把 Custom 默认配为 openai）
            if (meta.Provider == AIProvider.Custom
                && meta.ProtocolKind == ProtocolKind.OpenAI
                && result.Success)
            {
                return result with { ProtocolNote = "Custom 假定 OpenAI 兼容协议；如服务端用其它协议请检查" };
            }
            return result;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new TestResult(false, $"请求超时（{RequestTimeout.TotalSeconds:0}s）", (int)sw.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return new TestResult(false, $"网络错误：{Truncate(ex.Message, 200)}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult(false, $"异常：{Truncate(ex.Message, 200)}", (int)sw.ElapsedMilliseconds);
        }
    }

    private static async Task<TestResult> SendAnthropicAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/v1/messages";
        using var http = new HttpClient { Timeout = RequestTimeout };
        var sw = Stopwatch.StartNew();

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);
        req.Content = JsonContent.Create(new
        {
            model,
            max_tokens = 16,
            messages = new[] { new { role = "user", content = "say 'ok'" } }
        });

        var resp = await http.SendAsync(req, ct);
        sw.Stop();
        var latency = (int)sw.ElapsedMilliseconds;

        if (IsSuccess(resp.StatusCode))
        {
            return new TestResult(true, $"OK · {latency}ms", latency);
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new TestResult(false, $"HTTP {(int)resp.StatusCode}：{Truncate(body, 150)}", latency);
    }

    private static async Task<TestResult> SendOpenAIAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/chat/completions";
        using var http = new HttpClient { Timeout = RequestTimeout };
        var sw = Stopwatch.StartNew();

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(new
        {
            model,
            max_tokens = 16,
            messages = new[] { new { role = "user", content = "say 'ok'" } }
        });

        var resp = await http.SendAsync(req, ct);
        sw.Stop();
        var latency = (int)sw.ElapsedMilliseconds;

        if (IsSuccess(resp.StatusCode))
        {
            return new TestResult(true, $"OK · {latency}ms", latency);
        }
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new TestResult(false, $"HTTP {(int)resp.StatusCode}：{Truncate(body, 150)}", latency);
    }

    private static bool IsSuccess(System.Net.HttpStatusCode code)
        => (int)code >= 200 && (int)code < 300;

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}